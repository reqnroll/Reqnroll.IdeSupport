
namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Tests for <see cref="GherkinDocumentContextCalculator"/> covering the two public-to-internals
/// surface methods: GetBackgroundStepsWithContexts and GetScenarioOutlineStepsWithContexts.
/// </summary>
public class GherkinDocumentContextCalculatorTests
{
    private readonly ITelemetryService _monitoring = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private IdeSupportGherkinDocument ParseFeature(string text)
    {
        var dialect = ReqnrollGherkinDialectProvider.Get("en");
        var parser = new IdeSupportGherkinParser(dialect, _monitoring);
        parser.ParseAndCollectErrors(text, _logger, out var doc, out _);
        return doc;
    }

    // ── GetBackgroundStepsWithContexts ────────────────────────────────────────

    [Fact]
    public void Background_step_context_deduplicates_by_tag_signature()
    {
        // Two un-tagged scenarios share the same tag-matching scope, so the calculator
        // deduplicates them to a single context entry (one match attempt per distinct tag set).
        const string text = """
            Feature: F
            Background:
              Given a background step
            Scenario: S1
              Given s1 step
            Scenario: S2
              Given s2 step
            """;

        var doc = ParseFeature(text);
        var feature = doc.Feature;
        var background = feature.Children.OfType<Background>().Single();
        var bgStep = background.Steps.First();

        var featureCtx = new SimpleContext(null!, feature);
        var bgCtx = new SimpleContext(featureCtx, background);

        var results = GherkinDocumentContextCalculator
            .GetBackgroundStepsWithContexts(bgStep, bgCtx)
            .ToList();

        // Both scenarios have no tags → identical tag-matching scope → deduped to one entry
        results.Should().HaveCount(1);
        results.Single().Key.Should().Be(bgStep.Text);
    }

    [Fact]
    public void Background_step_context_is_expanded_per_distinct_tag_combination()
    {
        // When scenarios have different tags, each has a distinct matching scope and is NOT deduped
        const string text = """
            Feature: F
            Background:
              Given a background step
            @tagA
            Scenario: S1
              Given s1 step
            @tagB
            Scenario: S2
              Given s2 step
            """;

        var doc = ParseFeature(text);
        var feature = doc.Feature;
        var background = feature.Children.OfType<Background>().Single();
        var bgStep = background.Steps.First();

        var featureCtx = new SimpleContext(null!, feature);
        var bgCtx = new SimpleContext(featureCtx, background);

        var results = GherkinDocumentContextCalculator
            .GetBackgroundStepsWithContexts(bgStep, bgCtx)
            .ToList();

        // @tagA and @tagB are distinct → two separate context entries
        results.Should().HaveCount(2);
        results.Should().AllSatisfy(kv => kv.Key.Should().Be(bgStep.Text));
    }

    [Fact]
    public void Background_step_with_no_scenarios_returns_single_feature_context()
    {
        // A feature with a background but no scenarios: the feature context is used as the scope
        const string text = """
            Feature: F
            Background:
              Given a background step
            """;

        var doc = ParseFeature(text);
        var feature = doc.Feature;
        var background = feature.Children.OfType<Background>().Single();
        var bgStep = background.Steps.First();

        var featureCtx = new SimpleContext(null!, feature);
        var bgCtx = new SimpleContext(featureCtx, background);

        var results = GherkinDocumentContextCalculator
            .GetBackgroundStepsWithContexts(bgStep, bgCtx)
            .ToList();

        results.Should().HaveCount(1);
        results.Single().Key.Should().Be(bgStep.Text);
    }

    // ── GetScenarioOutlineStepsWithContexts ───────────────────────────────────

    [Fact]
    public void ScenarioOutline_step_is_expanded_per_example_row()
    {
        const string text = """
            Feature: F
            Scenario Outline: SO
              Given I have <count> items
              Examples:
                | count |
                | 3     |
                | 7     |
            """;

        var doc = ParseFeature(text);
        var outline = doc.Feature.Children.OfType<ScenarioOutline>().Single();
        var step = outline.Steps.First();

        var featureCtx = new SimpleContext(null!, doc.Feature);
        var outlineCtx = new SimpleContext(featureCtx, outline);

        var results = GherkinDocumentContextCalculator
            .GetScenarioOutlineStepsWithContexts(step, outlineCtx)
            .ToList();

        results.Should().HaveCount(2);
        results[0].Key.Should().Be("I have 3 items");
        results[1].Key.Should().Be("I have 7 items");
    }

    [Fact]
    public void ScenarioOutline_step_with_multiple_placeholders_are_all_replaced()
    {
        const string text = """
            Feature: F
            Scenario Outline: SO
              Given <a> plus <b>
              Examples:
                | a | b |
                | 1 | 2 |
            """;

        var doc = ParseFeature(text);
        var outline = doc.Feature.Children.OfType<ScenarioOutline>().Single();
        var step = outline.Steps.First();

        var featureCtx = new SimpleContext(null!, doc.Feature);
        var outlineCtx = new SimpleContext(featureCtx, outline);

        var results = GherkinDocumentContextCalculator
            .GetScenarioOutlineStepsWithContexts(step, outlineCtx)
            .ToList();

        results.Single().Key.Should().Be("1 plus 2");
    }

    [Fact]
    public void ScenarioOutline_with_empty_examples_returns_original_step_text()
    {
        // An outline with no example rows: should return the raw step text with placeholders
        const string text = """
            Feature: F
            Scenario Outline: SO
              Given I have <count> items
              Examples:
                | count |
            """;

        var doc = ParseFeature(text);
        var outline = doc.Feature.Children.OfType<ScenarioOutline>().Single();
        var step = outline.Steps.First();

        var featureCtx = new SimpleContext(null!, doc.Feature);
        var outlineCtx = new SimpleContext(featureCtx, outline);

        var results = GherkinDocumentContextCalculator
            .GetScenarioOutlineStepsWithContexts(step, outlineCtx)
            .ToList();

        // No body rows → falls back to original placeholder text
        results.Should().ContainSingle()
            .Which.Key.Should().Be("I have <count> items");
    }

    [Fact]
    public void ScenarioOutline_tagged_examples_produce_separate_context_per_tag_group()
    {
        const string text = """
            Feature: F
            Scenario Outline: SO
              Given <x>
              @tagA
              Examples: set1
                | x |
                | 1 |
              @tagB
              Examples: set2
                | x |
                | 2 |
            """;

        var doc = ParseFeature(text);
        var outline = doc.Feature.Children.OfType<ScenarioOutline>().Single();
        var step = outline.Steps.First();

        var featureCtx = new SimpleContext(null!, doc.Feature);
        var outlineCtx = new SimpleContext(featureCtx, outline);

        var results = GherkinDocumentContextCalculator
            .GetScenarioOutlineStepsWithContexts(step, outlineCtx)
            .ToList();

        results.Should().HaveCount(2);
        results[0].Key.Should().Be("1");
        results[1].Key.Should().Be("2");
    }

    // ── minimal IGherkinDocumentContext helper ─────────────────────────────────

    private sealed class SimpleContext(IGherkinDocumentContext parent, object node) : IGherkinDocumentContext
    {
        public IGherkinDocumentContext Parent { get; } = parent;
        public object Node { get; } = node;
    }
}
