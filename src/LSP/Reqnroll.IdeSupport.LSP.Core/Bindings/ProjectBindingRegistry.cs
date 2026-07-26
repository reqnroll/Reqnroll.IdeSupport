using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gherkin.Ast;
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
        var sdMatches = StepDefinitions.Select(sd => sd.Match(step, context, stepText)).Where(m => m != null).ToArray();
        if (!sdMatches.Any())
            return new[] {MatchResultItem.CreateUndefined(step, stepText)};

        sdMatches = HandleDataTableOverloads(step, sdMatches);
        sdMatches = HandleDocStringOverloads(step, sdMatches);
        sdMatches = HandleArgumentlessOverloads(step, sdMatches);
        sdMatches = HandleScopeOverloads(sdMatches);

        if (sdMatches.Length == 1)
            return new[] {sdMatches[0]};

        return sdMatches.Select(mi => mi.CloneToAmbiguousItem()).ToArray();
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
        var stepDefinitionParser = new StepDefinitionFileParser();
        var projectStepDefinitionBindings = await stepDefinitionParser.Parse(stepDefinitionFile);
        return Where(binding => !IsSameSourceFile(binding.Implementation.SourceLocation?.SourceFile, stepDefinitionFile.FullName))
            .WithStepDefinitions(projectStepDefinitionBindings);
    }

    /// <summary>
    /// Replaces all step definitions and hooks originating from the given C# source file with
    /// freshly discovered ones, leaving bindings from other files untouched. This is the
    /// per-file replacement used by Roslyn/C# source-level binding discovery.
    /// </summary>
    public async Task<ProjectBindingRegistry> ReplaceBindings(CSharpStepDefinitionFile stepDefinitionFile)
    {
        var stepDefinitionParser = new StepDefinitionFileParser();
        var parsed = await stepDefinitionParser.ParseBindings(stepDefinitionFile);

        bool FromOtherFile(ProjectBinding binding) =>
            !IsSameSourceFile(binding.Implementation.SourceLocation?.SourceFile, stepDefinitionFile.FullName);

        return new ProjectBindingRegistry(
            StepDefinitions.Where(FromOtherFile).Concat(parsed.StepDefinitions),
            Hooks.Where(FromOtherFile).Concat(parsed.Hooks));
    }

    /// <summary>
    /// Compares the step-definition expressions for <paramref name="sourceFile"/> between
    /// <paramref name="before"/> and <paramref name="after"/>, keyed by
    /// <c>(StepDefinitionType, Method, ParameterTypes)</c> rather than source line -- an edit
    /// elsewhere in the file shifts line numbers without changing binding identity, and line
    /// number is deliberately excluded from this comparison. Returns <see langword="true"/> if a
    /// binding for this file was added, removed, or had its matched expression change; edits to
    /// method bodies, comments, or anything else that doesn't touch a step's matched expression
    /// report no change.
    /// </summary>
    /// <remarks>
    /// A method can carry multiple attributes of the same step type with the same parameter
    /// types but different expression text (e.g. two <c>[When(...)]</c> on one method), which
    /// collapse to the same key. Bindings are therefore grouped by key and compared as a sorted
    /// multiset of expressions per key, rather than a single expression per key.
    /// </remarks>
    public static bool HasExpressionChanges(
        ProjectBindingRegistry before, ProjectBindingRegistry after, string sourceFile)
    {
        static string Key(ProjectStepDefinitionBinding b) =>
            $"{b.StepDefinitionType}|{b.Implementation.Method}|{string.Join(",", b.Implementation.ParameterTypes)}";

        bool OwnedByFile(ProjectStepDefinitionBinding b) =>
            IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, sourceFile);

        static Dictionary<string, List<string>> GroupExpressionsByKey(IEnumerable<ProjectStepDefinitionBinding> bindings) =>
            bindings.GroupBy(Key).ToDictionary(
                g => g.Key,
                g => g.Select(b => b.Expression).OrderBy(e => e, StringComparer.Ordinal).ToList());

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
