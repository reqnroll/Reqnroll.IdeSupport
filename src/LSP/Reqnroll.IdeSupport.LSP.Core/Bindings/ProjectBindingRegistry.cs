using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// The immutable set of step definitions and hooks discovered for a project, plus the matching
/// logic (regex/scope/overload resolution) that resolves a Gherkin step or scenario against
/// them. Each mutation (connector refresh, Roslyn per-file patch) produces a new instance with
/// a bumped <see cref="Version"/>.
/// </summary>
[DebuggerDisplay("{Version}_{ProjectHash}")]
public record ProjectBindingRegistry
{
    private const string DataTableDefaultTypeName = TypeShortcuts.ReqnrollTableType;
    private const string DocStringDefaultTypeName = TypeShortcuts.StringType;
    /// <summary>Sentinel registry used before any bindings have been discovered for a project.</summary>
    public static ProjectBindingRegistry Invalid = new(ImmutableArray<ProjectStepDefinitionBinding>.Empty, ImmutableArray<ProjectHookBinding>.Empty);

    private static ProjectBindingImplementationEqualityComparer _equalityComparerForProjectBindingImplementations = new();
    private static int _versionCounter;

    // Keyed by instance rather than an instance field: ProjectBindingRegistry is a record, whose
    // synthesized equality compares every instance field, so a cache field here would silently
    // break structural equality (harmless today -- nothing compares registries structurally --
    // but a footgun worth avoiding). A registry is immutable and replaced wholesale on every
    // mutation, so the index only ever needs building once per instance (issue #471).
    private static readonly ConditionalWeakTable<ProjectBindingRegistry, StepLiteralIndex> _literalIndexCache = new();

    // Stateless (all fields are static readonly), so one shared instance avoids a fresh
    // allocation on every ReplaceStepDefinitions/ReplaceBindings call.
    private static readonly IStepDefinitionFileParser StepDefinitionParser = new StepDefinitionFileParser();

    private StepLiteralIndex LiteralIndex =>
        _literalIndexCache.GetValue(this, r => StepLiteralIndex.Build(r.StepDefinitions));

    private ProjectBindingRegistry(IEnumerable<ProjectStepDefinitionBinding> stepDefinitions, IEnumerable<ProjectHookBinding> hooks)
    {
        StepDefinitions = stepDefinitions.ToImmutableArray();
        Hooks = hooks.ToImmutableArray();
    }

    /// <summary>Creates a registry from a full set of step definitions and hooks, tagged with the project's content hash.</summary>
    public ProjectBindingRegistry(IEnumerable<ProjectStepDefinitionBinding> stepDefinitions, IEnumerable<ProjectHookBinding> hooks, int projectHash)
        : this(stepDefinitions, hooks)
    {
        ProjectHash = projectHash;
    }

    /// <summary>A process-wide, monotonically increasing version, bumped on every new registry instance.</summary>
    public int Version { get; } = Interlocked.Increment(ref _versionCounter);
    /// <summary>The hash of the project's binding sources at the time of a full (connector/reflection) discovery, or null for a Roslyn patch.</summary>
    public int? ProjectHash { get; }
    /// <summary>True when this registry was produced by an incremental Roslyn per-file patch rather than a full discovery.</summary>
    public bool IsPatched => !ProjectHash.HasValue && this != Invalid;

    /// <summary>The step definitions in this registry.</summary>
    public ImmutableArray<ProjectStepDefinitionBinding> StepDefinitions { get; }
    /// <summary>The hooks in this registry.</summary>
    public ImmutableArray<ProjectHookBinding> Hooks { get; }

    /// <summary>Returns a short "ProjectBindingRegistry_V{Version}_H{ProjectHash}" identifier for diagnostics/logging.</summary>
    public override string ToString() => $"ProjectBindingRegistry_V{Version}_H{ProjectHash}";

    /// <summary>Returns the hooks (ordered by <see cref="HookType"/> then hook order) that apply to the given scenario.</summary>
    public HookMatchResult MatchScenarioToHooks(Scenario scenario, IGherkinDocumentContext context)
    {
        var hookMatches = Hooks
            .Where(h => h.IsValid && h.Match(scenario, context))
            .OrderBy(h => h.HookType)
            .ThenBy(h => h.HookOrder)
            .ToArray();

        return new HookMatchResult(hookMatches);
    }

    /// <summary>
    /// Matches a Gherkin step against this registry's step definitions, handling scenario
    /// outline placeholder substitution and background multi-scope matching as needed.
    /// </summary>
    public MatchResult MatchStep(Step step, IGherkinDocumentContext context)
    {
        var stepText = step.Text;
        if (context.IsScenarioOutline() && stepText.Contains("<"))
        {
            var stepsWithScopes = GherkinDocumentContextCalculator.GetScenarioOutlineStepsWithContexts(step, context);
            return MatchMultiScope(step, stepsWithScopes);
        }

        if (context.IsBackground())
        {
            var stepsWithScopes = GherkinDocumentContextCalculator.GetBackgroundStepsWithContexts(step, context);
            return MatchMultiScope(step, stepsWithScopes);
        }

        return MatchStep(step, context, stepText);
    }

    private MatchResult MatchStep(Step step, IGherkinDocumentContext context, string stepText) =>
        MatchResult.CreateMultiMatch(MatchSingleContextResult(step, context, stepText));

    private MatchResult MatchMultiScope(Step step,
        IEnumerable<KeyValuePair<string, IGherkinDocumentContext>> stepsWithScopes)
    {
        var matches = stepsWithScopes.Select(swc => MatchSingleContextResult(step, swc.Value, swc.Key))
            .SelectMany(m => m).ToArray();
        var multiMatches = MergeMultiMatches(matches);
        Debug.Assert(multiMatches.Length > 0); // MatchSingleContextResult returns undefined steps as well
        return MatchResult.CreateMultiMatch(multiMatches);
    }

    private MatchResultItem[] MergeMultiMatches(MatchResultItem[] matches)
    {
        var multiMatches = matches.GroupBy(m => m.Type).SelectMany(g =>
        {
            switch (g.Key)
            {
                case MatchResultType.Undefined:
                    return new[] {g.First()};
                case MatchResultType.Ambiguous:
                case MatchResultType.Defined:
                    return MergeSingularMatchResults(g);
                default:
                    throw new InvalidOperationException();
            }
        }).ToArray();
        return multiMatches;
    }

    private IEnumerable<MatchResultItem> MergeSingularMatchResults(IEnumerable<MatchResultItem> results)
    {
        foreach (var implGroup in results.GroupBy(r => r.MatchedStepDefinition.Implementation))
            // yielding the first with error or just the first if there were no errors
            yield return implGroup.FirstOrDefault(mri => mri.HasErrors) ?? implGroup.First();
    }

    private MatchResultItem[] MatchSingleContextResult(Step step, IGherkinDocumentContext context, string stepText)
    {
        // Literal prefilter (issue #471): narrows the O(bindings) regex-attempt loop below to
        // bindings whose statically-known literal text is actually present in this step, via one
        // Aho-Corasick scan instead of a per-binding regex attempt. See StepLiteralIndex's remarks
        // for why this can never exclude a binding that would have genuinely matched.
        var candidates = LiteralIndex.GetCandidates(stepText).ToArray();
        var sdMatches = candidates
            .Select(sd => sd.Match(step, context, stepText)).Where(m => m != null).ToArray();
        if (!sdMatches.Any())
            return new[] {MatchResultItem.CreateUndefined(step, stepText, FindNearMissErrors(candidates, step, context, stepText))};

        sdMatches = HandleDataTableOverloads(step, sdMatches);
        sdMatches = HandleDocStringOverloads(step, sdMatches);
        sdMatches = HandleArgumentlessOverloads(step, sdMatches);
        sdMatches = HandleScopeOverloads(sdMatches);

        if (sdMatches.Length == 1)
            return new[] {sdMatches[0]};

        return sdMatches.Select(mi => mi.CloneToAmbiguousItem()).ToArray();
    }

    /// <summary>
    /// Issue #514's "cheap first step": when a step has no valid match, checks whether any
    /// <em>invalid</em> candidate binding's regex/scope would otherwise have matched it (via
    /// <see cref="ProjectStepDefinitionBinding.WouldMatchIgnoringValidity"/>, which — unlike
    /// <see cref="ProjectStepDefinitionBinding.Match"/> — doesn't short-circuit on
    /// <c>!IsValid</c>) and, if so, returns that binding's <see cref="ProjectBinding.Error"/> so
    /// the step's "undefined" diagnostic can name the real reason instead of a generic "not
    /// found" — e.g. a step-definition method that lost its required <c>static</c> modifier is
    /// still reported as the specific validation failure, not silently as if no binding had ever
    /// existed for it. Returns <see langword="null"/> when no invalid candidate matches
    /// structurally, preserving the existing generic message.
    /// </summary>
    /// <remarks>
    /// <paramref name="candidates"/> is <see cref="LiteralIndex"/>'s own prefiltered set — already
    /// narrowed to bindings whose literal text could possibly appear in <paramref name="stepText"/>
    /// (soundly, per <see cref="StepLiteralIndex"/>'s remarks), so this re-checks only the regex
    /// and scope, not the literal prefilter.
    /// </remarks>
    private static string[]? FindNearMissErrors(
        IEnumerable<ProjectStepDefinitionBinding> candidates, Step step, IGherkinDocumentContext context, string stepText)
    {
        var errors = candidates
            .Where(b => b.Error != null && b.WouldMatchIgnoringValidity(step, context, stepText))
            .Select(b => b.Error)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return errors.Length == 0 ? null : errors;
    }

    /// <summary>
    ///     Selects DataTable overload, this can be eliminated later when we process conversions
    /// </summary>
    private MatchResultItem[] HandleDataTableOverloads(Step step, MatchResultItem[] sdMatches)
    {
        if (step.Argument is DataTable && sdMatches.Length > 1)
        {
            // assuming that sdMatches contains real matches, not match candidates (hints)
            Debug.Assert(sdMatches.All(m => m.Type == MatchResultType.Defined));
            var matchesWithDataTableParameter = sdMatches.Where(m =>
                m.ParameterMatch.DataTableParameterType == DataTableDefaultTypeName).ToArray();
            if (matchesWithDataTableParameter.Any())
                sdMatches = matchesWithDataTableParameter;
        }

        return sdMatches;
    }

    /// <summary>
    ///     Selects DocString overload, this can be eliminated later when we process conversions
    /// </summary>
    private MatchResultItem[] HandleDocStringOverloads(Step step, MatchResultItem[] sdMatches)
    {
        if (step.Argument is DocString && sdMatches.Length > 1)
        {
            // assuming that sdMatches contains real matches, not match candidates (hints)
            Debug.Assert(sdMatches.All(m => m.Type == MatchResultType.Defined));
            var matchesWithDocStringParameter = sdMatches.Where(m =>
                m.ParameterMatch.DocStringParameterType == DocStringDefaultTypeName).ToArray();
            if (matchesWithDocStringParameter.Any())
                sdMatches = matchesWithDocStringParameter;
        }

        return sdMatches;
    }

    /// <summary>
    ///     Selects argumentless overload, this can be eliminated later when we process conversions(?)
    /// </summary>
    private MatchResultItem[] HandleArgumentlessOverloads(Step step, MatchResultItem[] sdMatches)
    {
        if (step.Argument == null && sdMatches.Length > 1)
        {
            // assuming that sdMatches contains real matches, not match candidates (hints)
            Debug.Assert(sdMatches.All(m => m.Type == MatchResultType.Defined));

            var matchesWithoutParameterError = sdMatches.Where(m => !m.ParameterMatch.HasError).ToArray();
            if (matchesWithoutParameterError.Length == 1)
            {
                var candidatingMatch = matchesWithoutParameterError[0];
                if (sdMatches.All(m => m == candidatingMatch ||
                                       m.ParameterMatch.ParameterTypes.Length ==
                                       m.ParameterMatch.StepTextParameters.Length + 1))
                    return matchesWithoutParameterError;
            }
        }

        return sdMatches;
    }

    /// <summary>
    ///     Selects scoped overload
    /// </summary>
    private MatchResultItem[] HandleScopeOverloads(MatchResultItem[] sdMatches)
    {
        if (sdMatches.Length > 1)
        {
            // assuming that sdMatches contains real matches, not match candidates (hints)
            Debug.Assert(sdMatches.All(m => m.Type == MatchResultType.Defined));
            var matchesWithScope = sdMatches.Where(m =>
                m.MatchedStepDefinition.Scope != null).ToArray();
            if (matchesWithScope.Any())
            {
                // Group matches by everything except the Scope property
                // and take the first item from each group
                sdMatches = matchesWithScope
                    .GroupBy(m => m.MatchedStepDefinition.Implementation, _equalityComparerForProjectBindingImplementations)
                    .Select(g => g.First())
                    .ToArray();
            }
        }

        return sdMatches;
    }

    /// <summary>Creates a registry from step definitions and hooks with no known project hash (e.g. an incremental patch).</summary>
    public static ProjectBindingRegistry FromBindings(
        IEnumerable<ProjectStepDefinitionBinding> projectStepDefinitionBindings, IEnumerable<ProjectHookBinding>? hooks = null) => new(projectStepDefinitionBindings, hooks ?? Array.Empty<ProjectHookBinding>());

    /// <summary>Returns a new registry with the given step definitions appended to this one's, keeping the same hooks.</summary>
    public ProjectBindingRegistry WithStepDefinitions(
        IEnumerable<ProjectStepDefinitionBinding> projectStepDefinitionBindings)
    {
        var stepDefinitions = StepDefinitions.ToList();
        stepDefinitions.AddRange(projectStepDefinitionBindings);
        return new ProjectBindingRegistry(stepDefinitions, Hooks);
    }

    /// <summary>Returns a new registry with <paramref name="original"/> swapped for <paramref name="replacement"/>.</summary>
    public ProjectBindingRegistry ReplaceStepDefinition(ProjectStepDefinitionBinding original,
        ProjectStepDefinitionBinding replacement)
    {
        return new ProjectBindingRegistry(StepDefinitions.Select(sd => sd == original ? replacement : sd), Hooks);
    }

    /// <summary>Returns a new registry containing only the step definitions matching <paramref name="predicate"/>, keeping the same hooks.</summary>
    public ProjectBindingRegistry Where(Func<ProjectStepDefinitionBinding, bool> predicate) =>
        new(StepDefinitions.Where(predicate), Hooks);

    /// <summary>Re-parses <paramref name="stepDefinitionFile"/> and replaces its step definitions, leaving bindings from other files untouched.</summary>
    public async Task<ProjectBindingRegistry> ReplaceStepDefinitions(CSharpStepDefinitionFile stepDefinitionFile)
    {
        var projectStepDefinitionBindings = await StepDefinitionParser.Parse(stepDefinitionFile);
        return Where(binding => !IsSameSourceFile(binding.Implementation.SourceLocation?.SourceFile, stepDefinitionFile.FullName))
            .WithStepDefinitions(projectStepDefinitionBindings);
    }

    /// <summary>
    /// Replaces all step definitions and hooks originating from the given C# source file with
    /// freshly discovered ones, leaving bindings from other files untouched. This is the
    /// per-file replacement used by Roslyn/C# source-level binding discovery.
    /// </summary>
    /// <remarks>
    /// A binding is also dropped from the "other files" side when it's superseded by identity
    /// (see <see cref="BindingIdentity"/>) by one of the freshly parsed bindings, even if
    /// <see cref="IsSameSourceFile"/> says it comes from a different file. This is a safety net
    /// for issues #469/#503/#515: the reflection connector's and Roslyn's source paths for the
    /// very same file can legitimately disagree (a PDB path baked in from a devcontainer/CI build
    /// vs. the live LSP workspace path; a stale/unreadable PDB source location that resolves to
    /// null; a stale IDE-side cache), so path comparison alone can fail to recognize an existing
    /// binding as belonging to the file being reconciled -- leaving it in place alongside its
    /// freshly discovered replacement and surfacing as a false "ambiguous step" (the same method
    /// reported twice, once by its connector-style short name and once by its Roslyn-style
    /// fully-qualified name).
    /// </remarks>
    public async Task<ProjectBindingRegistry> ReplaceBindings(
        CSharpStepDefinitionFile stepDefinitionFile, Action<string>? diagnostics = null)
    {
        var parsed = await StepDefinitionParser.ParseBindings(stepDefinitionFile);

        bool FromOtherFile(ProjectBinding binding) =>
            !IsSameSourceFile(binding.Implementation.SourceLocation?.SourceFile, stepDefinitionFile.FullName);

        var newStepDefinitionIdentities = new HashSet<(ScenarioBlock StepDefinitionType, string Identity)>(
            parsed.StepDefinitions.Select(sd => (sd.StepDefinitionType, Identity: BindingIdentity(sd.Implementation))));
        var newHookIdentities = new HashSet<(HookType HookType, string Identity)>(
            parsed.Hooks.Select(h => (h.HookType, Identity: BindingIdentity(h.Implementation))));

        bool NotSupersededStepDefinition(ProjectStepDefinitionBinding sd) =>
            FromOtherFile(sd) &&
            !newStepDefinitionIdentities.Contains((sd.StepDefinitionType, BindingIdentity(sd.Implementation)));

        bool NotSupersededHook(ProjectHookBinding h) =>
            FromOtherFile(h) &&
            !newHookIdentities.Contains((h.HookType, BindingIdentity(h.Implementation)));

        // Diagnostic safety net for the #469/#503/#515 duplicate-bindings failure mode: an
        // existing binding whose source file *name* matches the file being reconciled but whose
        // full path doesn't (SameSourceFile=false) is exactly the shape that identity requires
        // is caught. Silent in the overwhelmingly common case (nothing matches by name alone);
        // only logs when that shape is actually present, so it's cheap to leave in permanently
        // rather than needing to be specially deployed to catch a rare recurrence. Superseded
        // should always read true here — it's the signal to watch for a false reading of that.
        if (diagnostics != null)
        {
            var targetFileName = Path.GetFileName(stepDefinitionFile.FullName);
            var suspects = StepDefinitions
                .Where(sd => string.Equals(
                    sd.Implementation.SourceLocation?.SourceFile is { } f ? Path.GetFileName(f) : null,
                    targetFileName, StringComparison.OrdinalIgnoreCase))
                .Where(FromOtherFile)
                .ToList();

            if (suspects.Count > 0)
            {
                diagnostics(
                    $"[DIAG-DUP] '{stepDefinitionFile.FullName}': {suspects.Count} existing binding(s) " +
                    "share this file's name under a different path — verifying identity-based supersede:");
                foreach (var sd in suspects)
                {
                    var identity = BindingIdentity(sd.Implementation);
                    var superseded = newStepDefinitionIdentities.Contains((sd.StepDefinitionType, identity));
                    diagnostics(
                        $"[DIAG-DUP]   {sd.StepDefinitionType} Method='{sd.Implementation.Method}' " +
                        $"Identity='{identity}' OldSourceFile='{sd.Implementation.SourceLocation?.SourceFile}' " +
                        $"Superseded={superseded}");
                }
            }
        }

        return new ProjectBindingRegistry(
            StepDefinitions.Where(NotSupersededStepDefinition).Concat(parsed.StepDefinitions),
            Hooks.Where(NotSupersededHook).Concat(parsed.Hooks));
    }

    /// <summary>
    /// A path-independent identity for a binding's implementation: its method identity (see
    /// <see cref="NormalizeMethodIdentity"/>) plus its parameter types, each normalized (see
    /// <see cref="NormalizeParameterType"/>). Used by <see cref="ReplaceBindings"/> to recognize
    /// that an existing binding and a freshly parsed one describe the same method even when their
    /// source-file paths don't match.
    /// </summary>
    private static string BindingIdentity(ProjectBindingImplementation implementation) =>
        $"{NormalizeMethodIdentity(implementation.Method)}|" +
        $"{string.Join(",", implementation.ParameterTypes.Select(NormalizeParameterType))}";

    private static readonly Dictionary<string, string> CSharpKeywordToClrTypeName = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean", ["byte"] = "Byte", ["sbyte"] = "SByte", ["char"] = "Char",
        ["decimal"] = "Decimal", ["double"] = "Double", ["float"] = "Single",
        ["int"] = "Int32", ["uint"] = "UInt32", ["long"] = "Int64", ["ulong"] = "UInt64",
        ["short"] = "Int16", ["ushort"] = "UInt16", ["string"] = "String", ["object"] = "Object",
    };

    // Matches each dotted identifier chain within a parameter-type string (the type itself, and
    // every generic type argument), so a generic/array/nullable type's namespace-qualified pieces
    // -- e.g. the "System.Collections.Generic"/"String"/"Int32" inside
    // "System.Collections.Generic.Dictionary<String,Int32>" -- are each reduced independently,
    // rather than only the string's own trailing segment (which a plain Split('.').Last() would
    // do, incorrectly leaving everything up to the first type argument's namespace untouched).
    private static readonly Regex DottedIdentifierRegex =
        new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

    /// <summary>
    /// Reduces a parameter type to a spelling-independent canonical form, for the same reason
    /// <see cref="NormalizeMethodIdentity"/> reduces a method name: the connector reports a
    /// parameter's fully-qualified CLR type name (e.g. "System.Int32", "Reqnroll.DataTable",
    /// "System.Collections.Generic.List&lt;String&gt;", "System.Int32?" -- effectively
    /// <c>Type.FullName</c>, confirmed by running the actual connector's discovery command against
    /// a probe assembly), while Roslyn reports the literal source-code type text (e.g. "int",
    /// "DataTable", "List&lt;string&gt;", "int?" -- <c>ParameterSyntax.Type.ToString()</c>), which
    /// for a C# primitive keyword doesn't even share a spelling with its CLR name, and can vary in
    /// namespace-qualification and whitespace independently of the connector's. This strips
    /// whitespace, then reduces every dotted identifier chain in the string -- the type itself
    /// *and* each of its generic type arguments -- to its trailing segment, mapping a C# primitive
    /// keyword to its CLR simple name; array (<c>[]</c>), generic (<c>&lt;,&gt;</c>), and nullable
    /// (<c>?</c>) punctuation is left as-is, which is already spelled identically on both sides.
    /// </summary>
    /// <remarks>
    /// Confirmed live against the Quickstart sample (issue #515 follow-up): fixing only the
    /// top-level type name (treating the whole string as one identifier) corrected parameterless
    /// and simple-typed methods, but left every generic/array/nullable-typed method duplicated --
    /// e.g. the connector's "System.Collections.Generic.Dictionary&lt;String,Int32&gt;" never
    /// matched Roslyn's "Dictionary&lt;string, int&gt;" even after that narrower fix, since neither
    /// the inner type arguments' spelling nor the space after the comma were normalized.
    /// </remarks>
    private static string NormalizeParameterType(string type)
    {
        var withoutWhitespace = WhitespaceRegex.Replace(type, string.Empty);
        return DottedIdentifierRegex.Replace(withoutWhitespace, match =>
        {
            var simpleName = match.Value.Split('.').Last();
            return CSharpKeywordToClrTypeName.TryGetValue(simpleName, out var clrName) ? clrName : simpleName;
        });
    }

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Reduces a binding's <see cref="ProjectBindingImplementation.Method"/> to its trailing
    /// "DeclaringType.MethodName" segment, discarding any parameter-signature suffix and
    /// namespace prefix.
    /// </summary>
    /// <remarks>
    /// The reflection connector and Roslyn format this string differently for the very same
    /// method: the connector emits "DeclaringType.MethodName(ParamType, ...)" (built from
    /// <c>MethodInfo</c> reflection data, no namespace --
    /// see <c>DiscoveryResultTransformer.GetMethodReference</c>), while Roslyn emits
    /// "Namespace.DeclaringType.MethodName" (walking up syntax-tree ancestors -- see
    /// <c>StepDefinitionFileParser.FullMethodName</c>), with no parameter list. A literal
    /// comparison of the two therefore never matches even for the identical method, which is why
    /// <see cref="BindingIdentity"/> normalizes through this method instead of comparing
    /// <see cref="ProjectBindingImplementation.Method"/> directly.
    /// </remarks>
    private static string NormalizeMethodIdentity(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return string.Empty;

        var withoutSignature = method!.Split('(')[0];
        var segments = withoutSignature.Split('.');
        return segments.Length <= 2
            ? withoutSignature
            : string.Join(".", segments.Skip(segments.Length - 2));
    }

    /// <summary>
    /// Compares the step-definition expressions for <paramref name="sourceFile"/> between
    /// <paramref name="before"/> and <paramref name="after"/>, keyed by
    /// <c>(StepDefinitionType, Method, ParameterTypes)</c> rather than source line -- an edit
    /// elsewhere in the file shifts line numbers without changing binding identity, and line
    /// number is deliberately excluded from this comparison. Returns <see langword="true"/> if a
    /// binding for this file was added, removed, had its matched expression change, or had its
    /// <see cref="ProjectBinding.Error"/> change; edits to method bodies, comments, or anything
    /// else that doesn't touch either report no change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method can carry multiple attributes of the same step type with the same parameter
    /// types but different expression text (e.g. two <c>[When(...)]</c> on one method), which
    /// collapse to the same key. Bindings are therefore grouped by key and compared as a sorted
    /// multiset of expression/error pairs per key, rather than a single expression per key.
    /// </para>
    /// <para>
    /// <see cref="ProjectBinding.Error"/> is included (issue #514) because a binding's validity
    /// affects matching independent of its expression text: <c>ProjectStepDefinitionBinding.Match</c>
    /// returns <see langword="null"/> whenever <c>!IsValid</c>, so a binding transitioning
    /// valid⇄invalid (e.g. a step-definition method losing/gaining a required <c>static</c>
    /// modifier, with no expression text touched at all) changes which steps this binding
    /// matches -- and, for .cs binding-validation diagnostics specifically, is the only thing
    /// that changed at all. Without this, that edit would report no change and callers relying
    /// on this method to decide whether to notify (<c>ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync</c>)
    /// would silently skip it.
    /// </para>
    /// </remarks>
    public static bool HasExpressionChanges(
        ProjectBindingRegistry before, ProjectBindingRegistry after, string sourceFile)
    {
        static string Key(ProjectStepDefinitionBinding b) =>
            $"{b.StepDefinitionType}|{b.Implementation.Method}|{string.Join(",", b.Implementation.ParameterTypes)}";

        static string Signature(ProjectStepDefinitionBinding b) =>
            $"{b.Expression}|{b.Error}";

        bool OwnedByFile(ProjectStepDefinitionBinding b) =>
            IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, sourceFile);

        static Dictionary<string, List<string>> GroupExpressionsByKey(IEnumerable<ProjectStepDefinitionBinding> bindings) =>
            bindings.GroupBy(Key).ToDictionary(
                g => g.Key,
                g => g.Select(Signature).OrderBy(s => s, StringComparer.Ordinal).ToList());

        var beforeByKey = GroupExpressionsByKey(before.StepDefinitions.Where(OwnedByFile));
        var afterByKey  = GroupExpressionsByKey(after.StepDefinitions.Where(OwnedByFile));

        if (beforeByKey.Count != afterByKey.Count)
            return true;

        foreach (var entry in beforeByKey)
        {
            if (!afterByKey.TryGetValue(entry.Key, out var newExpressions) || !newExpressions.SequenceEqual(entry.Value))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compares the hook bindings for <paramref name="sourceFile"/> between
    /// <paramref name="before"/> and <paramref name="after"/>, keyed by
    /// <c>(HookType, Method, ParameterTypes)</c> the same way <see cref="HasExpressionChanges"/>
    /// keys step definitions. Returns <see langword="true"/> if a hook for this file was added,
    /// removed, had its scope or order change, or had its <see cref="ProjectBinding.Error"/>
    /// change; edits to method bodies, comments, or anything else that doesn't touch any of those
    /// report no change.
    /// </summary>
    /// <remarks>
    /// Added alongside <see cref="HasExpressionChanges"/> to close a gap where hook-only edits
    /// (e.g. adding <c>[BeforeScenario]</c>, changing its tag scope) were silently re-discovered
    /// into the live registry by Roslyn but never triggered a
    /// <c>BindingRegistryChangedNotification</c> — since <see cref="ConnectorBindingRegistryProvider"/>
    /// only checked step-definition expressions, the hook-count CodeLens stayed stale until the
    /// next full rebuild. There is no single "expression" for a hook the way there is for a step
    /// definition, so scope (formatted via <see cref="Documents.BindingScope.ToString"/>) and
    /// order are compared instead — the two hook properties that affect what actually fires.
    /// <see cref="ProjectBinding.Error"/> was added to this signature for the same reason
    /// <see cref="HasExpressionChanges"/>'s remarks give: a hook transitioning valid⇄invalid
    /// (e.g. losing/gaining a required <c>static</c> modifier for a non-scenario-scoped hook
    /// type) changes whether it actually fires, with no scope/order text touched at all.
    /// </remarks>
    public static bool HasHookChanges(
        ProjectBindingRegistry before, ProjectBindingRegistry after, string sourceFile)
    {
        static string Key(ProjectHookBinding b) =>
            $"{b.HookType}|{b.Implementation.Method}|{string.Join(",", b.Implementation.ParameterTypes)}";

        static string Signature(ProjectHookBinding b) =>
            $"{b.Scope?.ToString() ?? string.Empty}|{b.HookOrder}|{b.Error}";

        bool OwnedByFile(ProjectHookBinding b) =>
            IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, sourceFile);

        static Dictionary<string, List<string>> GroupSignaturesByKey(IEnumerable<ProjectHookBinding> bindings) =>
            bindings.GroupBy(Key).ToDictionary(
                g => g.Key,
                g => g.Select(Signature).OrderBy(s => s, StringComparer.Ordinal).ToList());

        var beforeByKey = GroupSignaturesByKey(before.Hooks.Where(OwnedByFile));
        var afterByKey  = GroupSignaturesByKey(after.Hooks.Where(OwnedByFile));

        if (beforeByKey.Count != afterByKey.Count)
            return true;

        foreach (var entry in beforeByKey)
        {
            if (!afterByKey.TryGetValue(entry.Key, out var newSignatures) || !newSignatures.SequenceEqual(entry.Value))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compares two source-file paths for identity. The comparison normalizes the paths and is
    /// case-insensitive: the reflection connector records source paths from the PDB (often with an
    /// upper-case drive letter), while Roslyn discovery derives them from an LSP document URI (which
    /// can carry a lower-case drive letter). A case-sensitive compare would treat those as different
    /// files and fail to replace a file's previous bindings, leaving a stale binding behind.
    /// </summary>
    internal static bool IsSameSourceFile(string? sourceFile, string targetFullName)
    {
        if (string.IsNullOrEmpty(sourceFile) || string.IsNullOrEmpty(targetFullName))
            return false;

        return string.Equals(NormalizePath(sourceFile!), NormalizePath(targetFullName),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether this registry already has at least one step-definition or hook binding whose
    /// source location points at <paramref name="filePath"/>. Used to distinguish "this file was
    /// already reconciled" from "a connector run merely happened for its project" — see issue #517.
    /// </summary>
    public bool HasAnyBindingFor(string filePath)
    {
        return StepDefinitions.Any(b => IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, filePath)) ||
            Hooks.Any(b => IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, filePath));
    }

    /// <summary>
    /// Finds the first step-definition binding whose source location covers
    /// <paramref name="location"/> (same file, line within leeway — see
    /// <see cref="BindingLocationMatcher.CoversQuery"/>). When the binding was syntax-discovered
    /// (<see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/> is set), uses the exact
    /// attribute line for matching; otherwise falls back to a heuristic line window to account
    /// for attributes above the method declaration.
    /// Returns <see langword="null"/> when no binding matches.
    /// </summary>
    public ProjectStepDefinitionBinding? FindBindingAtLocation(SourceLocation location)
    {
        return StepDefinitions
            .FirstOrDefault(b => b.Implementation.SourceLocation != null &&
                BindingLocationMatcher.CoversQuery(b, location));
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
