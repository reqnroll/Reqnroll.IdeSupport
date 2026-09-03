using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Issue #568: <see cref="HookMatching"/> is the shared logic behind "Go to Hooks" and the
/// hook-count CodeLens — cumulative vs. own-level hook-type resolution, and the offset-based
/// context resolution whose comments explicitly reference a prior off-by-one bug (#101) — with
/// zero direct test coverage anywhere in the codebase.
/// </summary>
public class HookMatchingTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    public HookMatchingTests() => _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());

    private const string SampleFeature = "Feature: F\nScenario: S\n    Given a step\n";

    private IReadOnlyCollection<IdeSupportTag> ParseTags(string text) =>
        new IdeSupportTagParser(_logger, _telemetryService, _configProvider)
            .Parse(new StubGherkinTextSnapshot(text), ProjectBindingRegistry.Invalid);

    private static ProjectHookBinding MakeHook(
        HookType hookType, BindingScope? scope = null, int? hookOrder = null, string method = "MyHook") =>
        new(new ProjectBindingImplementation(method, null, new SourceLocation("Hooks.cs", 5, 1)),
            scope, hookType, hookOrder, error: null);

    // ── GetApplicableHookTypes (cumulative) ───────────────────────────────────────

    [Fact]
    public void GetApplicableHookTypes_for_Feature_returns_only_feature_level_hooks()
    {
        var types = HookMatching.GetApplicableHookTypes(HookContextLevel.Feature);

        types.Should().BeEquivalentTo(new[]
        {
            HookType.BeforeTestRun, HookType.AfterTestRun, HookType.BeforeFeature, HookType.AfterFeature
        });
    }

    [Fact]
    public void GetApplicableHookTypes_for_Scenario_includes_feature_level_hooks_cumulatively()
    {
        var types = HookMatching.GetApplicableHookTypes(HookContextLevel.Scenario);

        types.Should().Contain(HookType.BeforeTestRun).And.Contain(HookType.BeforeScenario);
        types.Should().NotContain(HookType.BeforeStep);
    }

    [Fact]
    public void GetApplicableHookTypes_for_Step_includes_feature_and_scenario_level_hooks_cumulatively()
    {
        var types = HookMatching.GetApplicableHookTypes(HookContextLevel.Step);

        types.Should().Contain(HookType.BeforeTestRun)
            .And.Contain(HookType.BeforeScenario)
            .And.Contain(HookType.BeforeStep)
            .And.Contain(HookType.BeforeScenarioBlock);
    }

    [Fact]
    public void GetApplicableHookTypes_for_an_unknown_level_throws()
    {
        var act = () => HookMatching.GetApplicableHookTypes((HookContextLevel)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── GetOwnLevelHookTypes (non-cumulative) ─────────────────────────────────────

    [Fact]
    public void GetOwnLevelHookTypes_for_Scenario_excludes_feature_level_hooks()
    {
        var types = HookMatching.GetOwnLevelHookTypes(HookContextLevel.Scenario);

        types.Should().BeEquivalentTo(new[] { HookType.BeforeScenario, HookType.AfterScenario });
    }

    [Fact]
    public void GetOwnLevelHookTypes_for_Step_excludes_feature_and_scenario_level_hooks()
    {
        var types = HookMatching.GetOwnLevelHookTypes(HookContextLevel.Step);

        types.Should().BeEquivalentTo(new[]
        {
            HookType.BeforeScenarioBlock, HookType.AfterScenarioBlock, HookType.BeforeStep, HookType.AfterStep
        });
    }

    [Fact]
    public void GetOwnLevelHookTypes_for_Feature_is_the_same_as_the_cumulative_set()
    {
        HookMatching.GetOwnLevelHookTypes(HookContextLevel.Feature).Should()
            .BeEquivalentTo(HookMatching.GetApplicableHookTypes(HookContextLevel.Feature));
    }

    [Fact]
    public void GetOwnLevelHookTypes_for_an_unknown_level_throws()
    {
        var act = () => HookMatching.GetOwnLevelHookTypes((HookContextLevel)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── ResolveContext ─────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveContext_at_the_step_line_resolves_to_Step_level()
    {
        var tags = ParseTags(SampleFeature);
        var offset = SampleFeature.IndexOf("Given a step", StringComparison.Ordinal);

        var (level, contextTag) = HookMatching.ResolveContext(tags, offset);

        level.Should().Be(HookContextLevel.Step);
        contextTag.Should().NotBeNull();
    }

    [Fact]
    public void ResolveContext_at_the_scenario_line_resolves_to_Scenario_level()
    {
        var tags = ParseTags(SampleFeature);
        var offset = SampleFeature.IndexOf("Scenario: S", StringComparison.Ordinal);

        var (level, contextTag) = HookMatching.ResolveContext(tags, offset);

        level.Should().Be(HookContextLevel.Scenario);
    }

    [Fact]
    public void ResolveContext_at_the_feature_line_resolves_to_Feature_level()
    {
        var tags = ParseTags(SampleFeature);
        var offset = SampleFeature.IndexOf("Feature: F", StringComparison.Ordinal);

        var (level, contextTag) = HookMatching.ResolveContext(tags, offset);

        level.Should().Be(HookContextLevel.Feature);
    }

    [Fact]
    public void ResolveContext_beyond_the_end_of_the_document_resolves_to_None()
    {
        var tags = ParseTags(SampleFeature);

        var (level, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.Length + 1000);

        level.Should().Be(HookContextLevel.None);
        contextTag.Should().BeNull();
    }

    [Fact]
    public void ResolveContext_at_the_very_last_character_of_a_line_still_resolves_it()
    {
        // Regression guard for the #101-shaped off-by-one: the block spans are treated as
        // inclusive of their end offset so a click at end-of-line still resolves.
        var tags = ParseTags(SampleFeature);
        var stepLineEnd = SampleFeature.IndexOf('\n', SampleFeature.IndexOf("Given a step", StringComparison.Ordinal));

        var (level, _) = HookMatching.ResolveContext(tags, stepLineEnd);

        level.Should().Be(HookContextLevel.Step);
    }

    // ── ResolveMatchingHooks ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveMatchingHooks_cumulative_at_Step_level_includes_feature_and_scenario_hooks()
    {
        var tags = ParseTags(SampleFeature);
        var (_, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.IndexOf("Given a step", StringComparison.Ordinal));

        var registry = new ProjectBindingRegistry(Array.Empty<ProjectStepDefinitionBinding>(), new[]
        {
            MakeHook(HookType.BeforeTestRun, method: "M1"),
            MakeHook(HookType.BeforeFeature, method: "M2"),
            MakeHook(HookType.BeforeScenario, method: "M3"),
            MakeHook(HookType.BeforeStep, method: "M4"),
        }, 0);

        var result = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Step, contextTag);

        result.Select(h => h.HookType).Should().BeEquivalentTo(new[]
        {
            HookType.BeforeTestRun, HookType.BeforeFeature, HookType.BeforeScenario, HookType.BeforeStep
        });
    }

    [Fact]
    public void ResolveMatchingHooks_own_level_only_at_Step_level_excludes_feature_and_scenario_hooks()
    {
        var tags = ParseTags(SampleFeature);
        var (_, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.IndexOf("Given a step", StringComparison.Ordinal));

        var registry = new ProjectBindingRegistry(Array.Empty<ProjectStepDefinitionBinding>(), new[]
        {
            MakeHook(HookType.BeforeTestRun, method: "M1"),
            MakeHook(HookType.BeforeFeature, method: "M2"),
            MakeHook(HookType.BeforeScenario, method: "M3"),
            MakeHook(HookType.BeforeStep, method: "M4"),
        }, 0);

        var result = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Step, contextTag, ownLevelOnly: true);

        result.Select(h => h.HookType).Should().BeEquivalentTo(new[] { HookType.BeforeStep });
    }

    [Fact]
    public void ResolveMatchingHooks_excludes_invalid_hooks()
    {
        var tags = ParseTags(SampleFeature);
        var (_, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.IndexOf("Feature: F", StringComparison.Ordinal));

        var invalidHook = new ProjectHookBinding(
            new ProjectBindingImplementation("Bad", null, new SourceLocation("Hooks.cs", 5, 1)),
            scope: null, HookType.BeforeFeature, hookOrder: null, error: "boom");
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), new[] { invalidHook }, 0);

        var result = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Feature, contextTag);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveMatchingHooks_excludes_hooks_scoped_to_a_different_feature_title()
    {
        var tags = ParseTags(SampleFeature);
        var (_, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.IndexOf("Feature: F", StringComparison.Ordinal));

        var registry = new ProjectBindingRegistry(Array.Empty<ProjectStepDefinitionBinding>(), new[]
        {
            MakeHook(HookType.BeforeFeature, scope: new BindingScope { FeatureTitle = "SomeOtherFeature" }),
        }, 0);

        var result = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Feature, contextTag);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveMatchingHooks_orders_same_type_hooks_by_hook_order()
    {
        var tags = ParseTags(SampleFeature);
        var (_, contextTag) = HookMatching.ResolveContext(tags, SampleFeature.IndexOf("Feature: F", StringComparison.Ordinal));

        var registry = new ProjectBindingRegistry(Array.Empty<ProjectStepDefinitionBinding>(), new[]
        {
            MakeHook(HookType.BeforeFeature, hookOrder: 20, method: "Second"),
            MakeHook(HookType.BeforeFeature, hookOrder: 5, method: "First"),
        }, 0);

        var result = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Feature, contextTag);

        result.Select(h => h.Implementation.Method).Should().ContainInOrder("First", "Second");
    }
}
