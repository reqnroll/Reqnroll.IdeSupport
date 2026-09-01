using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;

/// <summary>
/// Implements the scan behind the custom <c>reqnroll/findUnusedStepDefinitions</c> request.
/// <para>
/// Scans all supplied project binding registries and returns one row per unused
/// <em>binding expression</em>. A C# method decorated with multiple step attributes produces
/// multiple <see cref="ProjectStepDefinitionBinding"/> objects; each expression is checked
/// independently — used expressions are omitted, unused ones each produce a row.
/// </para>
/// <para>
/// Uses <see cref="IBindingMatchService.FindUsages(BindingId,IReadOnlyCollection{ProjectOwner})"/>
/// with no project filter (intersection semantics: an expression is considered used if any
/// project's feature files reference it) — an O(1) reverse-index lookup per binding (issue #471),
/// keyed by <see cref="BindingId"/> so it's inherently per-expression-specific; no separate
/// per-location cache or post-hoc expression filter is needed. Bindings sharing the same
/// <see cref="BindingId"/> across multiple registries are deduplicated to a single row.
/// </para>
/// <para>
/// The same binding can legitimately land in more than one project's registry: a project's own
/// discovery run reflects everything loaded into its discovery process, which includes bindings
/// declared in a <em>referenced</em> assembly (e.g. a class library another project also
/// separately discovers on its own). Left unattributed, the surviving row after dedup could end
/// up credited to whichever registry happened to be enumerated first — typically the referencing
/// project, not the one that actually declares the binding. When more than one registry reports
/// the same <see cref="BindingId"/>, the row is attributed to whichever candidate's own project
/// folder actually contains the binding's resolved source file (issue #547) — falling back to
/// first-seen only when no candidate's folder contains it (an unresolved source, or a binding
/// whose declaring project isn't loaded in this workspace at all).
/// </para>
/// </summary>
public sealed class FindUnusedStepDefinitionsService : IFindUnusedStepDefinitionsService
{
    private readonly IBindingMatchService _matchService;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="FindUnusedStepDefinitionsService"/> class.</summary>
    public FindUnusedStepDefinitionsService(IBindingMatchService matchService, IIdeSupportLogger logger)
    {
        _matchService = matchService;
        _logger = logger;
    }

    /// <remarks>
    /// A C# method may carry multiple step attributes ([Given("A")][When("B")]). Each attribute is
    /// a separate <see cref="ProjectStepDefinitionBinding"/> with its own <c>Expression</c>. The
    /// method produces one FAR row per UNUSED expression — so a method with one used and one unused
    /// expression yields a single row (the unused one). A method all of whose expressions are used
    /// produces no rows; a method none of whose expressions are used produces one row per expression.
    ///
    /// Cross-project deduplication: the same binding appearing in multiple project registries
    /// (a linked .cs file, or a referenced assembly's bindings picked up by the referencing
    /// project's own discovery run — issue #547) would otherwise produce duplicate rows for the
    /// same expression; <see cref="PickOwningCandidate"/> collapses these to one, attributed to
    /// whichever project's own folder actually contains the source.
    /// </remarks>
    public IReadOnlyList<UnusedStepDefinition> FindUnusedStepDefinitions(
        IReadOnlyList<(string ProjectName, string ProjectFolder, ProjectBindingRegistry Registry)> registries)
    {
        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsService: scanning {registries.Count} project(s)");

        // First pass: one candidate per BindingId, picking whichever registry most plausibly owns
        // it when the same binding is reported by more than one (see PickOwningCandidate).
        var candidates = new Dictionary<BindingId, (string ProjectName, string ProjectFolder, ProjectStepDefinitionBinding Binding)>();

        foreach (var (projectName, projectFolder, registry) in registries)
        {
            if (registry == ProjectBindingRegistry.Invalid) continue;

            foreach (var sd in registry.StepDefinitions)
            {
                if (!sd.IsValid) continue;
                if (sd.Implementation?.SourceLocation is null) continue;

                var bindingId = BindingId.For(sd);

                candidates[bindingId] = candidates.TryGetValue(bindingId, out var existing)
                    ? PickOwningCandidate(existing, (projectName, projectFolder, sd))
                    : (projectName, projectFolder, sd);
            }
        }

        var items = new List<UnusedStepDefinition>();

        foreach (var (projectName, _, sd) in candidates.Values)
        {
            var bindingId = BindingId.For(sd);
            var isExpressionUsed = _matchService.FindUsages(bindingId, null).Count > 0;
            if (isExpressionUsed) continue;

            var loc = sd.Implementation!.SourceLocation!;
            var (className, methodName) = ParseMethod(sd.Implementation.Method);

            // The entry is still reported — "this step definition is unused" is true and useful
            // regardless of where its source lives — but it travels with IsResolved so the
            // client can say why it cannot be opened instead of appearing to do nothing when
            // the user clicks it (issue #540).
            if (!loc.IsResolved)
                _logger.LogInfo(
                    $"FindUnusedStepDefinitionsService: '{className}.{methodName}' is unused, but the " +
                    $"compiled assembly records it at '{loc.RecordedSourceFile}', which does not exist on " +
                    "this machine — it will be listed as not openable. Rebuild the project locally.");

            items.Add(new UnusedStepDefinition(
                ProjectName: projectName,
                ClassName: className,
                MethodName: methodName,
                // Display-only field (shown in the Find Unused Step Definitions result list) —
                // uses DisplayExpression, not the `expression` identity key above, so a
                // method-name-style binding's raw auto-generated regex isn't shown (issue #344).
                BindingExpression: sd.DisplayExpression,
                // Null, not the recorded path, when it cannot be opened here: every client
                // already guards SourceFile for emptiness before navigating, so this alone stops
                // a click from silently going nowhere. IsResolved/RecordedSourceFile are what let
                // a client go further and explain it.
                SourceFile: loc.IsResolved ? loc.SourceFile : null,
                SourceLine: loc.SourceFileLine,
                SourceColumn: loc.SourceFileColumn,
                IsResolved: loc.IsResolved,
                // Suppressed only when it would duplicate a SourceFile the client already has.
                // When unresolved there is no SourceFile to duplicate and this is the only path
                // the client gets, so it must always be sent — nulling it there would leave the
                // client with nothing to show but "it didn't work".
                RecordedSourceFile: loc.IsResolved
                                    && PathUtils.IsSamePath(loc.SourceFile, loc.RecordedSourceFile)
                    ? null
                    : loc.RecordedSourceFile));
        }

        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsService: found {items.Count} unused step definition(s)");

        return items;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Chooses which of two candidates reporting the same <see cref="BindingId"/> "owns" it for
    /// attribution purposes (issue #547).
    /// </summary>
    /// <remarks>
    /// The reflection connector reports every binding loaded into its discovery process, not just
    /// ones declared in the assembly it was pointed at — so a project that references another
    /// Reqnroll-bearing project (a class library, say) has that library's own step definitions
    /// show up in its registry too, alongside the library's own separately-discovered registry
    /// reporting the very same binding under its own project name. <paramref name="candidate"/>
    /// replaces <paramref name="existing"/> only when it is the folder owner and the existing one
    /// is not — i.e. only a strict improvement. When neither is (an unresolved source location, or
    /// a binding whose true owner isn't a project loaded in this workspace at all), the existing,
    /// first-seen candidate is kept — preserving prior behaviour for that case.
    /// </remarks>
    private static (string ProjectName, string ProjectFolder, ProjectStepDefinitionBinding Binding) PickOwningCandidate(
        (string ProjectName, string ProjectFolder, ProjectStepDefinitionBinding Binding) existing,
        (string ProjectName, string ProjectFolder, ProjectStepDefinitionBinding Binding) candidate)
    {
        if (!IsFolderOwner(existing.ProjectFolder, existing.Binding) && IsFolderOwner(candidate.ProjectFolder, candidate.Binding))
            return candidate;

        return existing;
    }

    /// <summary>
    /// True when <paramref name="binding"/>'s resolved source file physically lives under
    /// <paramref name="projectFolder"/> — i.e. this project directly owns the source, rather than
    /// merely having discovered the binding by way of a referenced assembly.
    /// </summary>
    private static bool IsFolderOwner(string projectFolder, ProjectStepDefinitionBinding binding)
    {
        var loc = binding.Implementation?.SourceLocation;
        if (loc is null || !loc.IsResolved) return false;

        return PathUtils.IsUnderFolder(
            PathUtils.NormalizeForComparison(loc.SourceFile),
            PathUtils.NormalizeForComparison(projectFolder));
    }

    /// <summary>
    /// Parses ClassName and MethodName from the stored Method string.
    /// <list type="bullet">
    ///   <item>Connector path: <c>"ClassName.MethodName(paramType1, paramType2)"</c></item>
    ///   <item>Roslyn path: <c>"Namespace.ClassName.MethodName"</c></item>
    /// </list>
    /// In both cases: strip params, split on <c>.</c>, last segment = MethodName,
    /// second-to-last = ClassName.
    /// </summary>
    internal static (string ClassName, string MethodName) ParseMethod(string? method)
    {
        if (string.IsNullOrEmpty(method) || method == "???")
            return ("(unknown)", "(unknown)");

        // Strip parameter list: "ClassName.MethodName(int, string)" → "ClassName.MethodName"
        var parenIdx = method!.IndexOf('(');
        var withoutParams = parenIdx >= 0 ? method.Substring(0, parenIdx) : method;

        var parts = withoutParams.Split('.');
        if (parts.Length == 1)
            return ("(unknown)", parts[0]);

        // Second-to-last = ClassName (handles both Roslyn multi-segment and connector forms)
        var methodName = parts[parts.Length - 1];
        var className = parts[parts.Length - 2];
        return (className, methodName);
    }
}
