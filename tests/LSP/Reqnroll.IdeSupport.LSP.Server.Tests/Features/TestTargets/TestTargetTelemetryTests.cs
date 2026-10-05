#nullable enable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using GherkinLocation = Gherkin.Ast.Location;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestTargets;

/// <summary>Classification of the <c>Kind</c> telemetry property (issue #849).</summary>
public class TestTargetTelemetryTests
{
    // Line 0 "Feature: F", 1 "Rule: R", 2 "Scenario: S", 3 "Scenario Outline: O", 4 "Examples:", 5 "| a |", 6 "| 1 |"
    private const string Text =
        "Feature: F\nRule: R\nScenario: S\nScenario Outline: O\nExamples:\n| a |\n| 1 |\n";

    private static readonly LspTextSnapshot Snapshot = new("file:///t.feature", 1, Text);

    private static int Offset(int line) => Text.Split('\n').Take(line).Sum(l => l.Length + 1);

    private static GherkinRange Line(int line) =>
        GherkinRange.FromPoint(Snapshot, Offset(line), Text.Split('\n')[line].Length);

    private static GherkinRange Lines(int first, int last) =>
        new(Snapshot, Offset(first), Offset(last) + Text.Split('\n')[last].Length - Offset(first));

    private static readonly IdeSupportTag ScenarioTag = new(
        IdeSupportTagTypes.ScenarioDefinitionBlock, Lines(2, 2),
        new Scenario(Array.Empty<Tag>(), new GherkinLocation(3, 1), "Scenario", "S", "", Array.Empty<Step>(), Array.Empty<Examples>()));

    private static readonly Examples ExamplesNode = new(
        Array.Empty<Tag>(), new GherkinLocation(5, 1), "Examples", "", "",
        new TableRow(new GherkinLocation(6, 1), new[] { new TableCell(new GherkinLocation(6, 3), "a") }),
        new[] { new TableRow(new GherkinLocation(7, 1), new[] { new TableCell(new GherkinLocation(7, 3), "1") }) });

    private static readonly IdeSupportTag OutlineTag = new(
        IdeSupportTagTypes.ScenarioDefinitionBlock, Lines(3, 6),
        new ScenarioOutline(Array.Empty<Tag>(), new GherkinLocation(4, 1), "Scenario Outline", "O", "", Array.Empty<Step>(),
            new[] { ExamplesNode }));

    private static readonly IdeSupportTag ExamplesTag = new(IdeSupportTagTypes.ExamplesBlock, Lines(4, 6), ExamplesNode);

    private static readonly IdeSupportTag FeatureTag = new(IdeSupportTagTypes.FeatureBlock, Lines(0, 6));
    private static readonly IdeSupportTag RuleTag = new(IdeSupportTagTypes.RuleBlock, Lines(1, 6));

    private static readonly IdeSupportTag[] Tags = { FeatureTag, RuleTag, ScenarioTag, OutlineTag, ExamplesTag };

    [Fact]
    public void A_plain_scenario_is_classified_as_Scenario() =>
        TestTargetTelemetry.ClassifyScenario(Tags, Line(2)).Should().Be("Scenario");

    [Fact]
    public void A_scenario_outline_header_is_classified_as_Outline() =>
        TestTargetTelemetry.ClassifyScenario(Tags, Line(3)).Should().Be("Outline");

    [Fact]
    public void An_examples_body_row_is_classified_as_ExampleRow() =>
        TestTargetTelemetry.ClassifyScenario(Tags, Line(6)).Should().Be("ExampleRow");

    [Fact]
    public void The_examples_header_row_is_not_a_row_target_and_stays_Outline() =>
        TestTargetTelemetry.ClassifyScenario(Tags, Line(5)).Should().Be("Outline");

    [Fact]
    public void A_range_outside_every_scenario_has_no_kind() =>
        TestTargetTelemetry.ClassifyScenario(Tags, Line(0)).Should().BeNull();

    [Fact]
    public void A_range_inside_a_rule_is_classified_as_Rule() =>
        TestTargetTelemetry.ClassifyContainer(Tags, Lines(1, 6)).Should().Be("Rule");

    [Fact]
    public void The_whole_feature_is_classified_as_Feature() =>
        TestTargetTelemetry.ClassifyContainer(Tags, Lines(0, 6)).Should().Be("Feature");
}
