#nullable enable

using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.DocumentOutline;

public class GherkinDocumentSymbolServiceTests
{
    private static IReadOnlyCollection<DeveroomTag> ParseTags(string text)
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var monitoring = Substitute.For<ITelemetryService>();
        var configProvider = Substitute.For<IDeveroomConfigurationProvider>();
        configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
        var parser = new DeveroomTagParser(logger, monitoring, configProvider);
        return parser.Parse(new StubGherkinTextSnapshot(text), ProjectBindingRegistry.Invalid);
    }

    private static GherkinDocumentSymbolService CreateSut() => new();

    // ── Empty / no feature ────────────────────────────────────────────────────

    [Fact]
    public void Empty_tag_collection_returns_empty_list()
    {
        var result = CreateSut().BuildSymbols(Array.Empty<DeveroomTag>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void No_FeatureBlock_tag_returns_empty_list()
    {
        var tags = ParseTags(string.Empty);
        var result = CreateSut().BuildSymbols(tags);
        result.Should().BeEmpty();
    }

    // ── Feature ───────────────────────────────────────────────────────────────

    [Fact]
    public void Feature_only_returns_one_top_level_symbol()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildSymbols(tags);
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Feature_symbol_has_Feature_kind()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Kind.Should().Be(GherkinSymbolKind.Feature);
    }

    [Fact]
    public void Feature_symbol_name_is_feature_name()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Name.Should().Be("Calculator");
    }

    [Fact]
    public void Feature_with_no_scenarios_has_no_children()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children.Should().BeEmpty();
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scenario_is_a_child_of_Feature()
    {
        var tags = ParseTags("Feature: F\nScenario: Add\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public void Scenario_symbol_has_Scenario_kind()
    {
        var tags = ParseTags("Feature: F\nScenario: Add\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Kind.Should().Be(GherkinSymbolKind.Scenario);
    }

    [Fact]
    public void Scenario_symbol_name_is_scenario_name()
    {
        var tags = ParseTags("Feature: F\nScenario: Add two numbers\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Add two numbers");
    }

    [Fact]
    public void Multiple_scenarios_all_appear_as_siblings()
    {
        var text = "Feature: F\nScenario: S1\n    Given a step\nScenario: S2\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children.Should().HaveCount(2);
        result[0].Children.Select(c => c.Name).Should().BeEquivalentTo(new[] { "S1", "S2" });
    }

    // Regression test: an untitled "Scenario:" (no text after the colon) is valid Gherkin and is
    // also the transient state of a Scenario line while it's being typed. The Gherkin parser gives
    // it Name = "" (not null), which used to flow straight through to the LSP response, violating
    // the protocol's requirement that DocumentSymbol.name be non-empty — vscode-languageclient
    // rejects the whole response client-side ("name must not be falsy"), blanking the entire
    // Outline view rather than just this one symbol.
    [Fact]
    public void Untitled_scenario_symbol_name_falls_back_to_keyword()
    {
        var tags = ParseTags("Feature: F\nScenario:\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Scenario");
    }

    // ── Background ───────────────────────────────────────────────────────────

    [Fact]
    public void Background_is_a_child_of_Feature()
    {
        var text = "Feature: F\nBackground:\n    Given a step\nScenario: S\n    Given another step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children.Should().HaveCount(2);
    }

    [Fact]
    public void Background_symbol_has_Background_kind()
    {
        var text = "Feature: F\nBackground:\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Kind.Should().Be(GherkinSymbolKind.Background);
    }

    [Fact]
    public void Background_symbol_name_is_keyword()
    {
        var text = "Feature: F\nBackground:\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Background");
    }

    // ── Steps ────────────────────────────────────────────────────────────────

    [Fact]
    public void Step_is_a_child_of_Scenario()
    {
        var tags = ParseTags("Feature: F\nScenario: S\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public void Step_symbol_has_Step_kind()
    {
        var tags = ParseTags("Feature: F\nScenario: S\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Children[0].Kind.Should().Be(GherkinSymbolKind.Step);
    }

    [Fact]
    public void Step_symbol_name_includes_keyword_and_text()
    {
        var tags = ParseTags("Feature: F\nScenario: S\n    Given a step\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Children[0].Name.Should().Be("Given a step");
    }

    [Fact]
    public void Multiple_steps_all_appear_as_children_of_scenario()
    {
        var text = "Feature: F\nScenario: S\n    Given a step\n    When something happens\n    Then all is well\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Children.Should().HaveCount(3);
    }

    // ── Rule ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule_is_a_child_of_Feature()
    {
        var text = "Feature: F\nRule: My Rule\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Kind.Should().Be(GherkinSymbolKind.Rule);
    }

    [Fact]
    public void Rule_symbol_name_is_rule_name()
    {
        var text = "Feature: F\nRule: Business Rule\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Business Rule");
    }

    [Fact]
    public void Scenario_inside_Rule_is_child_of_Rule()
    {
        var text = "Feature: F\nRule: R\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        var rule = result[0].Children[0];
        rule.Children.Should().HaveCount(1);
        rule.Children[0].Kind.Should().Be(GherkinSymbolKind.Scenario);
    }

    // ── Scenario Outline ──────────────────────────────────────────────────────

    [Fact]
    public void ScenarioOutline_symbol_has_ScenarioOutline_kind()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Kind.Should().Be(GherkinSymbolKind.ScenarioOutline);
    }

    [Fact]
    public void ScenarioOutline_symbol_name_is_outline_name()
    {
        var text = "Feature: F\nScenario Outline: Calculator outline\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Calculator outline");
    }

    // Regression test: same class of bug as Untitled_scenario_symbol_name_falls_back_to_keyword,
    // for an untitled "Scenario Outline:".
    [Fact]
    public void Untitled_ScenarioOutline_symbol_name_falls_back_to_keyword()
    {
        var text = "Feature: F\nScenario Outline:\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        result[0].Children[0].Name.Should().Be("Scenario Outline");
    }

    // ── Examples ─────────────────────────────────────────────────────────────

    [Fact]
    public void Examples_is_a_child_of_ScenarioOutline()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        var outline = result[0].Children[0];
        outline.Children.Should().Contain(c => c.Kind == GherkinSymbolKind.Examples);
    }

    [Fact]
    public void Examples_symbol_has_Examples_kind()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        var outline = result[0].Children[0];
        var examples = outline.Children.First(c => c.Kind == GherkinSymbolKind.Examples);
        examples.Kind.Should().Be(GherkinSymbolKind.Examples);
    }

    [Fact]
    public void Examples_symbol_name_is_keyword_when_unnamed()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        var outline = result[0].Children[0];
        var examples = outline.Children.First(c => c.Kind == GherkinSymbolKind.Examples);
        examples.Name.Should().Be("Examples");
    }

    // ── Ranges ───────────────────────────────────────────────────────────────

    [Fact]
    public void Feature_symbol_range_starts_at_line_zero()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildSymbols(tags);
        result[0].Range.StartLinePosition.Line.Should().Be(0);
    }

    [Fact]
    public void SelectionRange_is_contained_within_Range()
    {
        var text = "Feature: F\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildSymbols(tags);
        var scenario = result[0].Children[0];
        scenario.SelectionRange.Start.Should().BeGreaterThanOrEqualTo(scenario.Range.Start);
        scenario.SelectionRange.End.Should().BeLessThanOrEqualTo(scenario.Range.End);
    }
}
