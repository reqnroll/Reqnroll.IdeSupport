using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefs;

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
/// <see cref="BindingId"/> across multiple registries (a linked .cs file in several projects)
/// are deduplicated.
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
    /// Cross-project deduplication: the same source file linked into multiple project registries
    /// would produce duplicate rows for the same expression; these are suppressed by the seen set.
    /// </remarks>
    public IReadOnlyList<UnusedStepDefinition> FindUnusedStepDefinitions(
        IReadOnlyList<(string ProjectName, ProjectBindingRegistry Registry)> registries)
    {
        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsService: scanning {registries.Count} project(s)");

        // Dedup by BindingId: a linked .cs file appearing in N project registries must not
        // produce N copies of the same row.
        var seen = new HashSet<BindingId>();

        var items = new List<UnusedStepDefinition>();

        foreach (var (projectName, registry) in registries)
        {
            if (registry == ProjectBindingRegistry.Invalid) continue;

            foreach (var sd in registry.StepDefinitions)
            {
                if (!sd.IsValid) continue;

                var loc = sd.Implementation?.SourceLocation;
                if (loc is null) continue;

                var bindingId = BindingId.For(sd);
                if (!seen.Add(bindingId)) continue;

                var isExpressionUsed = _matchService.FindUsages(bindingId, null).Count > 0;

                if (isExpressionUsed) continue;

                var (className, methodName) = ParseMethod(sd.Implementation!.Method);

                items.Add(new UnusedStepDefinition(
                    ProjectName: projectName,
                    ClassName: className,
                    MethodName: methodName,
                    // Display-only field (shown in the Find Unused Step Definitions result list) —
                    // uses DisplayExpression, not the `expression` identity key above, so a
                    // method-name-style binding's raw auto-generated regex isn't shown (issue #344).
                    BindingExpression: sd.DisplayExpression,
                    SourceFile: loc.SourceFile,
                    SourceLine: loc.SourceFileLine,
                    SourceColumn: loc.SourceFileColumn));
            }
        }

        _logger.LogVerbose(
            $"FindUnusedStepDefinitionsService: found {items.Count} unused step definition(s)");

        return items;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
