#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Folding;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Folding;

public class GherkinFoldingRangeServiceTests
{
    private static IReadOnlyCollection<IdeSupportTag> ParseTags(string text)
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var monitoring = Substitute.For<ITelemetryService>();
        var configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
        configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
        var parser = new IdeSupportTagParser(logger, monitoring, configProvider);
        return parser.Parse(new StubGherkinTextSnapshot(text), ProjectBindingRegistry.Invalid);
    }

    private static GherkinFoldingRangeService CreateSut() => new();

    // ── Empty / no feature ─────────────────────────────────────────────────

    [Fact]
    public void Empty_tag_collection_returns_empty_list()
    {
        var result = CreateSut().BuildFoldingRanges(Array.Empty<IdeSupportTag>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void No_FeatureBlock_tag_returns_empty_list()
    {
        var tags = ParseTags(string.Empty);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().BeEmpty();
    }

    // ── Feature only ───────────────────────────────────────────────────────

    [Fact]
    public void Feature_with_no_body_has_no_folds()
    {
        var tags = ParseTags("Feature: Calculator\n");
        var result = CreateSut().BuildFoldingRanges(tags);
        // A single-line feature has no lines after the keyword to fold
        result.Should().BeEmpty();
    }

    // ── Feature with scenario ──────────────────────────────────────────────

    [Fact]
    public void Feature_with_scenario_has_feature_body_fold()
    {
        var tags = ParseTags("Feature: F\nScenario: Add\n    Given a step\n");
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 2);
    }

    [Fact]
    public void Scenario_block_creates_folding_range()
    {
        var tags = ParseTags("Feature: F\nScenario: Add\n    Given a step\n");
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 2);
    }

    [Fact]
    public void Scenario_fold_includes_all_steps()
    {
        var text = "Feature: F\nScenario: Add\n    Given a step\n    When something\n    Then all good\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 4);
    }

    // ── Multiple scenarios ─────────────────────────────────────────────────

    [Fact]
    public void Multiple_scenarios_each_have_fold()
    {
        var text = "Feature: F\nScenario: S1\n    Given step1\nScenario: S2\n    Given step2\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 2)
              .And.Contain(r => r.StartLine == 3 && r.EndLine == 4);
    }

    // ── Background ─────────────────────────────────────────────────────────

    [Fact]
    public void Background_creates_folding_range()
    {
        var text = "Feature: F\nBackground:\n    Given a step\nScenario: S\n    Given another step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 2);
    }

    // ── Rule ───────────────────────────────────────────────────────────────

    [Fact]
    public void Rule_creates_folding_range()
    {
        var text = "Feature: F\nRule: My Rule\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 3);
    }

    [Fact]
    public void Scenario_inside_Rule_has_fold()
    {
        var text = "Feature: F\nRule: R\nScenario: S\n    Given a step\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 2 && r.EndLine == 3);
    }

    // ── Scenario Outline with Examples ─────────────────────────────────────

    [Fact]
    public void ScenarioOutline_creates_folding_range()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 5);
    }

    [Fact]
    public void Examples_block_creates_folding_range()
    {
        var text = "Feature: F\nScenario Outline: SO\n    Given the number is <n>\n    Examples:\n        | n |\n        | 1 |\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        result.Should().Contain(r => r.StartLine == 3 && r.EndLine == 5);
    }

    // ── Doc String ─────────────────────────────────────────────────────────

    [Fact]
    public void DocString_creates_folding_range()
    {
        // Doc string with content
        var text = "Feature: F\nScenario: Doc\n    Given a step\n    \"\"\"\n    some doc\n    string\n    \"\"\"\n    Then done\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        // The doc string lines: 3=\"\"\" (open), 4=content, 5=\"\"\" (close)
        // DocString range covers from first content line to last line of the step (after closing `"""`)
        result.Should().Contain(r => r.StartLine == 3 && r.EndLine == 6);
    }

    // ── Data Table ─────────────────────────────────────────────────────────

    [Fact]
    public void DataTable_creates_folding_range()
    {
        var text = "Feature: F\nScenario: Tabular\n    Given I have:\n        | a | b |\n        | 1 | 2 |\n    Then done\n";
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        // DataTable rows: line 3="| a | b |", line 4="| 1 | 2 |"
        result.Should().Contain(r => r.StartLine == 3 && r.EndLine == 4);
    }

    // ── Combined ───────────────────────────────────────────────────────────

    [Fact]
    public void Full_feature_produces_correct_fold_count()
    {
        var text = @"
Feature: F
  Scenario: S1
    Given step1
  Scenario: S2
    Given step2
"                      .TrimStart();
        var tags = ParseTags(text);
        var result = CreateSut().BuildFoldingRanges(tags);
        // Feature body fold + 2 scenario folds = 3 total
        result.Should().HaveCount(3);
    }
}
