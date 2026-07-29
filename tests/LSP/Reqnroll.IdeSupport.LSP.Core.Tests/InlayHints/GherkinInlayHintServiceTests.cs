using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.InlayHints;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.InlayHints;

/// <summary>
/// Unit tests for <see cref="GherkinInlayHintService"/> (F23). Construction of
/// <see cref="FeatureBindingMatchSet"/> uses real tag parsing (via <see cref="DeveroomTagParser"/>)
/// for the defined/undefined/ambiguous cases, matching <c>DiagnosticsAggregatorTests</c>'s
/// approach. The templated (Scenario Outline, varies-by-row) case is built directly against a
/// hand-crafted <see cref="MatchResult"/> since it represents what FromTags's row-merging already
/// produces for that scenario, without needing a full outline-example parsing pipeline.
/// </summary>
public class GherkinInlayHintServiceTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IDeveroomConfigurationProvider _configProvider = Substitute.For<IDeveroomConfigurationProvider>();

    private const string DocumentId = "file:///c:/proj/test.feature";

    public GherkinInlayHintServiceTests()
    {
        _configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
    }

    private GherkinInlayHintService CreateSut() => new();

    private static StubGherkinTextSnapshot Snap(string text) => new(text);

    private static ProjectStepDefinitionBinding Binding(string pattern, string method, string[]? parameterTypes = null) =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, parameterTypes, new SourceLocation("Steps.cs", 5, 1)));

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private FeatureBindingMatchSet MatchSetFor(string text, ProjectBindingRegistry? registry = null)
    {
        var parser = new DeveroomTagParser(_logger, _telemetryService, _configProvider);
        var reg = registry ?? RegistryWith();
        var tags = parser.Parse(Snap(text), reg);
        return FeatureBindingMatchSet.FromTags(DocumentId, 1, reg.Version, tags);
    }

    [Fact]
    public void Defined_step_with_a_unique_binding_produces_a_Binding_hint()
    {
        var registry = RegistryWith(Binding("the binding exists", "N.Steps.TheBindingExists", new[] { "int" }));
        const string feature = "Feature: F\nScenario: S\n    Given the binding exists\n";

        var hints = CreateSut().Build(MatchSetFor(feature, registry));

        var hint = hints.Should().ContainSingle().Subject;
        hint.Kind.Should().Be(GherkinInlayHintKind.Binding);
        hint.Label.Should().Be("→ Steps.TheBindingExists");
        hint.Tooltip.Should().Be("N.Steps.TheBindingExists(int)");
    }

    [Fact]
    public void Defined_step_with_a_connector_supplied_signature_embedded_in_Method_still_produces_a_clean_hint()
    {
        // Reflection-based connector discovery (issue #389) embeds the parameter list in Method
        // itself, e.g. "CalculatorStepDefinitions.WhenTheThreeNumbersAreAdded(Minimal.StepDefinitions.Verb)"
        // — the namespace-qualified parameter type's own dots must not corrupt the short-name split.
        var registry = RegistryWith(Binding(
            "the two numbers are added",
            "CalculatorStepDefinitions.WhenTheThreeNumbersAreAdded(Minimal.StepDefinitions.Verb)",
            new[] { "Minimal.StepDefinitions.Verb" }));
        const string feature = "Feature: F\nScenario: S\n    Given the two numbers are added\n";

        var hints = CreateSut().Build(MatchSetFor(feature, registry));

        var hint = hints.Should().ContainSingle().Subject;
        hint.Kind.Should().Be(GherkinInlayHintKind.Binding);
        hint.Label.Should().Be("→ CalculatorStepDefinitions.WhenTheThreeNumbersAreAdded");
        hint.Tooltip.Should().Be("CalculatorStepDefinitions.WhenTheThreeNumbersAreAdded(Minimal.StepDefinitions.Verb)");
    }

    [Fact]
    public void Undefined_step_produces_no_hint()
    {
        const string feature = "Feature: F\nScenario: S\n    Given step one\n";

        var hints = CreateSut().Build(MatchSetFor(feature));

        hints.Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_step_produces_a_match_count_hint_listing_candidates()
    {
        var b1 = Binding("ambiguous step", "N.M1");
        var b2 = Binding("ambiguous step", "N.M2");
        var registry = RegistryWith(b1, b2);
        const string feature = "Feature: F\nScenario: S\n  Given ambiguous step\n";

        var hints = CreateSut().Build(MatchSetFor(feature, registry));

        var hint = hints.Should().ContainSingle().Subject;
        hint.Kind.Should().Be(GherkinInlayHintKind.Ambiguous);
        hint.Label.Should().Be("→ 2 matches");
        hint.Tooltip.Should().Contain("N.M1").And.Contain("N.M2");
    }

    [Fact]
    public void Template_step_resolving_to_multiple_distinct_bindings_across_rows_produces_a_Templated_hint()
    {
        var snapshot = Snap("Feature: F\nScenario: S\n\tGiven a step\n");
        var range = GherkinRange.FromPoint(snapshot, startOffset: "Feature: F\nScenario: S\n\tGiven ".Length, length: "a step".Length);

        var b1 = Binding("a step", "N.RowOneSteps.M");
        var b2 = Binding("a step", "N.RowTwoSteps.M");
        // Each row's own match is a single, unambiguous Defined item; FromTags's row-merging is
        // what collapses the two rows' MatchResults into one MatchResult carrying both items —
        // reproduced directly here rather than via a full Scenario Outline parse.
        var result = MatchResult.CreateMultiMatch(new[]
        {
            MatchResultItem.CreateMatch(b1, ParameterMatch.NotMatch),
            MatchResultItem.CreateMatch(b2, ParameterMatch.NotMatch),
        });
        var match = new StepBindingMatch(DocumentId, range, result);
        var matchSet = new FeatureBindingMatchSet(DocumentId, ProjectOwner.Unknown, 1, 0, new[] { match });

        var hints = CreateSut().Build(matchSet);

        var hint = hints.Should().ContainSingle().Subject;
        hint.Kind.Should().Be(GherkinInlayHintKind.Templated);
        hint.Label.Should().Be("→ 2 bindings");
        hint.Tooltip.Should().Contain("N.RowOneSteps.M").And.Contain("N.RowTwoSteps.M");
    }

    [Fact]
    public void Multiple_steps_each_produce_their_own_hint_at_their_own_range()
    {
        var registry = RegistryWith(
            Binding("step one", "N.S1"),
            Binding("step two", "N.S2"));
        const string feature = "Feature: F\nScenario: S\n    Given step one\n    And step two\n";

        var hints = CreateSut().Build(MatchSetFor(feature, registry));

        hints.Should().HaveCount(2);
        hints.Select(h => h.Kind).Should().AllBeEquivalentTo(GherkinInlayHintKind.Binding);
    }
}
