using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="DiagnosticsAggregator"/>.
/// Construction of <see cref="FeatureBindingMatchSet"/> uses real tag parsing (via
/// <see cref="IdeSupportTagParser"/>) to stay in sync with how the server actually populates the
/// match cache — the aggregator should never need to be rewritten when parser internals change.
/// </summary>
public class DiagnosticsAggregatorTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    private const string DocumentId = "file:///c:/proj/test.feature";

    public DiagnosticsAggregatorTests()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DiagnosticsAggregator CreateSut() => new();

    private static StubGherkinTextSnapshot Snap(string text) => new(text);

    private static ProjectStepDefinitionBinding GivenBinding(string pattern) =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation("MyStep", null, new SourceLocation("Steps.cs", 5, 1)));

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private IReadOnlyCollection<IdeSupportTag> ParseTags(string text, ProjectBindingRegistry? registry = null)
    {
        var parser = new IdeSupportTagParser(_logger, _telemetryService, _configProvider);
        return parser.Parse(Snap(text), registry ?? RegistryWith());
    }

    private FeatureBindingMatchSet MatchSetFor(string text, ProjectBindingRegistry? registry = null)
    {
        var tags = ParseTags(text, registry);
        var reg = registry ?? RegistryWith();
        return FeatureBindingMatchSet.FromTags(DocumentId, 1, reg.Version, tags);
    }

    /// <summary>Builds a ParserError tag directly, as IdeSupportTagParser does when Gherkin is invalid.</summary>
    private static IdeSupportTag ParserErrorTag(IGherkinTextSnapshot snapshot, int start, int length, string message) =>
        new(IdeSupportTagTypes.ParserError, GherkinRange.FromPoint(snapshot, start, length), message);

    // ── F4: parse errors ──────────────────────────────────────────────────────

    [Fact]
    public void ParserError_tag_produces_Error_diagnostic_with_correct_message_and_source()
    {
        var snapshot = Snap("Scenario: no feature header\n");
        var tags = new[] { ParserErrorTag(snapshot, 0, 8, "unexpected token: Scenario") };

        var result = CreateSut().Aggregate(tags, FeatureBindingMatchSet.Empty);

        result.Should().ContainSingle();
        var diag = result[0];
        diag.Severity.Should().Be(GherkinDiagnosticSeverity.Error);
        diag.Source.Should().Be(DiagnosticsAggregator.ParserSource);
        diag.Message.Should().Be("unexpected token: Scenario");
    }

    [Fact]
    public void ParserError_tag_with_null_Data_uses_fallback_message()
    {
        var snapshot = Snap("bad\n");
        var tag = new IdeSupportTag(IdeSupportTagTypes.ParserError, GherkinRange.FromPoint(snapshot, 0, 3));
        // Data is null (default)

        var result = CreateSut().Aggregate(new[] { tag }, FeatureBindingMatchSet.Empty);

        result.Should().ContainSingle();
        result[0].Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ParserError_range_is_preserved_in_diagnostic()
    {
        var snapshot = Snap("Feature: X\nScenario: bad");
        var expectedRange = GherkinRange.FromPoint(snapshot, 11, 8);  // "Scenario:"
        var tag = new IdeSupportTag(IdeSupportTagTypes.ParserError, expectedRange, "error");

        var result = CreateSut().Aggregate(new[] { tag }, FeatureBindingMatchSet.Empty);

        result[0].Range.Should().BeSameAs(expectedRange);
    }

    [Fact]
    public void Multiple_ParserError_tags_each_produce_a_diagnostic()
    {
        var snapshot = Snap("bad bad bad\n");
        var tags = new[]
        {
            ParserErrorTag(snapshot, 0, 3, "error one"),
            ParserErrorTag(snapshot, 4, 3, "error two"),
        };

        var result = CreateSut().Aggregate(tags, FeatureBindingMatchSet.Empty);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Severity.Should().Be(GherkinDiagnosticSeverity.Error));
    }

    // ── F3: binding mismatches ────────────────────────────────────────────────

    [Fact]
    public void Undefined_step_produces_Warning_diagnostic_with_correct_message_and_source()
    {
        const string feature = "Feature: F\nScenario: S\n    Given a step with no binding\n";
        var matchSet = MatchSetFor(feature);  // empty registry → all steps undefined

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().ContainSingle();
        var diag = result[0];
        diag.Severity.Should().Be(GherkinDiagnosticSeverity.Warning);
        diag.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diag.Message.Should().Be(DiagnosticsAggregator.UndefinedStepMessage);
    }

    [Fact]
    public void Undefined_step_uses_an_invalid_near_miss_bindings_error_when_present()
    {
        // Issue #514's "cheap first step": a structurally-matching but invalid binding's Error
        // (set by StepDefinitionFileParser's validation, or by the connector for its own import
        // failures) should replace the generic "not found" message.
        var invalidBinding = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^the binding exists$"), null,
            new ProjectBindingImplementation("MyStep", null, new SourceLocation("Steps.cs", 5, 1)),
            error: "must be static");
        var registry = RegistryWith(invalidBinding);

        const string feature = "Feature: F\nScenario: S\n    Given the binding exists\n";
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().ContainSingle();
        var diag = result[0];
        diag.Severity.Should().Be(GherkinDiagnosticSeverity.Warning);
        diag.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diag.Message.Should().Be("must be static");
    }

    [Fact]
    public void Defined_step_does_not_produce_a_diagnostic()
    {
        const string feature = "Feature: F\nScenario: S\n    Given the binding exists\n";
        var registry = RegistryWith(GivenBinding("the binding exists"));
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_undefined_steps_each_produce_a_Warning_diagnostic()
    {
        const string feature = "Feature: F\nScenario: S\n    Given step one\n    And step two\n";
        var matchSet = MatchSetFor(feature);

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Severity.Should().Be(GherkinDiagnosticSeverity.Warning));
    }

    [Fact]
    public void Ambiguous_step_produces_Error_diagnostic_with_correct_message_and_source()
    {
        var b1 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method1", null, new SourceLocation("A.cs", 1, 1)));
        var b2 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method2", null, new SourceLocation("B.cs", 1, 1)));
        var registry = RegistryWith(b1, b2);

        const string feature = "Feature: F\nScenario: S\n  Given ambiguous step\n";
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().ContainSingle();
        var diag = result[0];
        diag.Severity.Should().Be(GherkinDiagnosticSeverity.Error);
        diag.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diag.Message.Should().Contain("Method1", "the hover message should list every colliding binding, not just say it's ambiguous");
        diag.Message.Should().Contain("Method2");
    }

    [Fact]
    public void Multiple_ambiguous_steps_each_produce_an_Error_diagnostic()
    {
        var b1 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method1", null, new SourceLocation("A.cs", 1, 1)));
        var b2 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method2", null, new SourceLocation("B.cs", 1, 1)));
        var registry = RegistryWith(b1, b2);

        const string feature = "Feature: F\nScenario: S\n  Given ambiguous step\n  And ambiguous step\n";
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), matchSet);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Severity.Should().Be(GherkinDiagnosticSeverity.Error));
    }

    // ── Combined ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_errors_and_undefined_steps_are_combined_in_a_single_list()
    {
        var snapshot = Snap("bad gherkin\n");
        var errorTag = ParserErrorTag(snapshot, 0, 3, "parse error");

        const string feature = "Feature: F\nScenario: S\n    Given no such step\n";
        var matchSet = MatchSetFor(feature);

        var result = CreateSut().Aggregate(new[] { errorTag }, matchSet);

        result.Should().HaveCount(2);
        result.Should().Contain(d => d.Severity == GherkinDiagnosticSeverity.Error);
        result.Should().Contain(d => d.Severity == GherkinDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Parse_errors_and_ambiguous_steps_are_combined_in_a_single_list()
    {
        var snapshot = Snap("bad gherkin\n");
        var errorTag = ParserErrorTag(snapshot, 0, 3, "parse error");

        var b1 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method1", null, new SourceLocation("A.cs", 1, 1)));
        var b2 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method2", null, new SourceLocation("B.cs", 1, 1)));
        var registry = RegistryWith(b1, b2);

        const string feature = "Feature: F\nScenario: S\n  Given ambiguous step\n";
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(new[] { errorTag }, matchSet);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.Severity.Should().Be(GherkinDiagnosticSeverity.Error));
    }

    // ── Empty / structural tags ───────────────────────────────────────────────

    [Fact]
    public void Empty_inputs_produce_no_diagnostics()
    {
        var result = CreateSut().Aggregate(Array.Empty<IdeSupportTag>(), FeatureBindingMatchSet.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Non_error_tags_do_not_produce_diagnostics()
    {
        const string feature = "Feature: F\nScenario: S\n    Given the binding exists\n";
        var registry = RegistryWith(GivenBinding("the binding exists"));
        var tags = ParseTags(feature, registry);  // FeatureBlock, StepBlock, DefinedStep, etc — no ParserError
        var matchSet = MatchSetFor(feature, registry);

        var result = CreateSut().Aggregate(tags, matchSet);

        result.Should().BeEmpty();
    }
}
