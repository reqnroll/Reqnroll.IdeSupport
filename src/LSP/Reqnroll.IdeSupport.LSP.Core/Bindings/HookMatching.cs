using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>The Gherkin context level a position/tag resolves to, for hook applicability.</summary>
public enum HookContextLevel { None, Feature, Scenario, Step }

/// <summary>
/// Shared hook-applicability/matching logic used by both "Go to Hooks" (single position →
/// applicable hooks) and the hook-count CodeLens (issue #269: every Feature/Scenario/Step line
/// in a document → applicable hook count) — kept in one place so the two never disagree about
/// what "applicable" means for a given context level.
/// </summary>
public static class HookMatching
{
    // Hook types visible at each context level, per the Hook Navigation design doc. Cumulative:
    // Feature-level hooks are also "applicable" at Scenario/Step level, since they're logically
    // in scope for every scenario/step even though they only fire once per feature.
    private static readonly HashSet<HookType> FeatureLevelHooks = new HashSet<HookType>
    {
        HookType.BeforeTestRun,  HookType.AfterTestRun,
        HookType.BeforeFeature,  HookType.AfterFeature,
    };

    private static readonly HashSet<HookType> ScenarioLevelHooks = new HashSet<HookType>(FeatureLevelHooks)
    {
        HookType.BeforeScenario, HookType.AfterScenario,
    };

    private static readonly HashSet<HookType> StepLevelHooks = new HashSet<HookType>(ScenarioLevelHooks)
    {
        HookType.BeforeScenarioBlock, HookType.AfterScenarioBlock,
        HookType.BeforeStep,          HookType.AfterStep,
    };

    /// <summary>Returns the hook types applicable at the given context level (cumulative, see remarks above).</summary>
    public static HashSet<HookType> GetApplicableHookTypes(HookContextLevel level) =>
        level switch
        {
            HookContextLevel.Feature  => FeatureLevelHooks,
            HookContextLevel.Scenario => ScenarioLevelHooks,
            HookContextLevel.Step     => StepLevelHooks,
            _                          => throw new ArgumentOutOfRangeException(nameof(level))
        };

    /// <summary>
    /// Returns every valid, applicable hook in <paramref name="registry"/> that matches
    /// <paramref name="contextTag"/>'s scope, ordered by <see cref="ProjectHookBinding.HookType"/>
    /// then <see cref="ProjectHookBinding.HookOrder"/>.
    /// </summary>
    public static IReadOnlyList<ProjectHookBinding> ResolveMatchingHooks(
        ProjectBindingRegistry registry, HookContextLevel level, DeveroomTag contextTag)
    {
        var applicableTypes = GetApplicableHookTypes(level);

        // ProjectHookBinding.Match does not use the Scenario argument — it only uses the
        // IGherkinDocumentContext for tag/scope matching — so null is safe to pass here.
        return registry.Hooks
            .Where(h => h.IsValid && applicableTypes.Contains(h.HookType))
            .Where(h => h.Match(null!, contextTag))
            .OrderBy(h => h.HookType)
            .ThenBy(h => h.HookOrder)
            .ToArray();
    }

    /// <summary>
    /// Determines the Gherkin context level at <paramref name="offset"/> from the flat
    /// <paramref name="tags"/> collection (produced by <c>DeveroomTagParser</c>) and returns
    /// the deepest matching tag for use as an <c>IGherkinDocumentContext</c> in scope matching.
    /// </summary>
    public static (HookContextLevel level, DeveroomTag contextTag) ResolveContext(
        IReadOnlyCollection<DeveroomTag> tags, int offset)
    {
        // Check from innermost to outermost: Step → Scenario → Feature.
        // A StepBlock hit means we're on a step line — use the enclosing ScenarioDefinitionBlock
        // as context because steps carry no tags; only scenario and feature blocks do.
        var stepTag = FindTag(tags, DeveroomTagTypes.StepBlock, offset);
        if (stepTag is not null)
        {
            var enclosingScenario = FindTag(tags, DeveroomTagTypes.ScenarioDefinitionBlock, offset);
            return (HookContextLevel.Step, enclosingScenario ?? stepTag);
        }

        var scenarioTag = FindTag(tags, DeveroomTagTypes.ScenarioDefinitionBlock, offset);
        if (scenarioTag is not null)
            return (HookContextLevel.Scenario, scenarioTag);

        var featureTag = FindTag(tags, DeveroomTagTypes.FeatureBlock, offset);
        if (featureTag is not null)
            return (HookContextLevel.Feature, featureTag);

        return (HookContextLevel.None, null!);
    }

    private static DeveroomTag? FindTag(IReadOnlyCollection<DeveroomTag> tags, string type, int offset)
        => tags.FirstOrDefault(t => t.Type == type && ContainsOffset(t, offset));

    // StepBlock/ScenarioDefinitionBlock/FeatureBlock tags are already full-line spans
    // (DeveroomTagParser.GetBlockSpan uses GherkinRange.FromLines, so Range.End is the offset
    // right past the last character of the block's last line). The upper bound must be
    // inclusive so a click at end-of-line still resolves — Gherkin is line-oriented, and this
    // is the same class of off-by-one that made Go to Definition miss in #101.
    private static bool ContainsOffset(DeveroomTag tag, int offset)
        => offset >= tag.Range.Start && offset <= tag.Range.End;
}
