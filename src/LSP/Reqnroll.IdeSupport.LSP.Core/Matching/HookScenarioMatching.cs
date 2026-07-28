#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// Resolves which project scenarios a given hook binding's scope matches -- the inverse of
/// <c>HookMatching</c> (issue #269, "given a position, which hooks apply"). Backs the
/// hook-match-count CodeLens and <c>reqnroll/goToMatchingScenarios</c> request (issue #373).
/// </summary>
public static class HookScenarioMatching
{
    /// <summary>
    /// True when <paramref name="hookType"/> has a per-scenario concept at all.
    /// <see cref="HookType.BeforeTestRun"/>/<see cref="HookType.AfterTestRun"/> fire once per test
    /// run regardless of any scenario's tags, so "N scenarios matched" is meaningless for them
    /// (decided in #373: suppress the lens/response entirely for these rather than showing a
    /// misleading count).
    /// </summary>
    public static bool IsScenarioCountable(HookType hookType) =>
        hookType is not (HookType.BeforeTestRun or HookType.AfterTestRun);

    /// <summary>
    /// Returns every scenario across <paramref name="matchSets"/> that <paramref name="hook"/>'s
    /// scope matches, deduplicated by (feature document, scenario start offset) so a `.feature`
    /// file linked into multiple projects doesn't count the same physical scenario twice.
    /// </summary>
    /// <remarks>
    /// A single per-scenario match call (<c>hook.Match(null!, scenarioTag)</c>) is correct for
    /// every hook type, not just scenario-scoped ones: <see cref="FeatureScenarioInfo.ScenarioTag"/>'s
    /// <see cref="GherkinDocumentContextExtensions.GetTagNames"/> already walks up to include
    /// inherited Feature-level tags, so a Feature-scoped hook's tag expression is evaluated
    /// against the right (scenario + feature) tag union with no special-casing needed.
    /// </remarks>
    public static IReadOnlyList<FeatureScenarioInfo> ResolveMatchingScenarios(
        IEnumerable<FeatureBindingMatchSet> matchSets, ProjectHookBinding hook)
    {
        if (!IsScenarioCountable(hook.HookType))
            return Array.Empty<FeatureScenarioInfo>();

        var seen = new HashSet<(string DocumentId, int Start)>();
        var result = new List<FeatureScenarioInfo>();

        foreach (var matchSet in matchSets)
        {
            foreach (var scenario in matchSet.Scenarios)
            {
                if (!hook.Match(null!, (IGherkinDocumentContext)scenario.ScenarioTag))
                    continue;
                if (!seen.Add((scenario.FeatureDocumentId, scenario.Range.Start)))
                    continue;

                result.Add(scenario);
            }
        }

        return result;
    }
}
