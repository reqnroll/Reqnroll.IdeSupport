using System.Collections.Generic;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.NavigationBar;

/// <summary>
/// Pure combo-population logic for the Navigation Bar (Issue #5 / Q22 Option B):
/// <see cref="GherkinNavigationBarLayout"/>.
/// </summary>
public class GherkinNavigationBarLayoutTests
{
    private static GherkinSymbolRange Range(int startLine, int endLine) =>
        new(new GherkinSymbolPosition(startLine, 0), new GherkinSymbolPosition(endLine, 0));

    private static GherkinSymbolNode Node(
        string name, int kind, int startLine, int endLine, IReadOnlyList<GherkinSymbolNode>? children = null) =>
        new(name, kind, Range(startLine, endLine), Range(startLine, startLine), children ?? System.Array.Empty<GherkinSymbolNode>());

    // Calculator.feature-shaped tree:
    // Feature (0-9)
    //   Scenario "Add two numbers" (5-7)
    //     Step "Given ..." (6-6)
    private static IReadOnlyList<GherkinSymbolNode> SingleScenarioTree()
    {
        var step = Node("Given the first number is 50", GherkinSymbolKinds.Step, 6, 6);
        var scenario = Node("Add two numbers", GherkinSymbolKinds.Scenario, 5, 7, new[] { step });
        var feature = Node("Calculator", GherkinSymbolKinds.Feature, 0, 9, new[] { scenario });
        return new[] { feature };
    }

    // WithBackgroundAndExample.feature-shaped tree:
    // Feature (0-17)
    //   Background (4-5)
    //     Step (5-5)
    //   ScenarioOutline "table driven scenarios" (8-16)
    //     Step (9-9)
    //     Examples (13-16)
    private static IReadOnlyList<GherkinSymbolNode> RuleAndBackgroundTree()
    {
        var bgStep = Node("Given the first number was 50", GherkinSymbolKinds.Step, 5, 5);
        var background = Node("we always start from a given first number", GherkinSymbolKinds.Background, 4, 5, new[] { bgStep });
        var scenarioStep = Node("Given the second no is 1", GherkinSymbolKinds.Step, 9, 9);
        var examples = Node("Examples", GherkinSymbolKinds.Examples, 13, 16);
        var outline = Node("table driven scenarios", GherkinSymbolKinds.Scenario, 8, 16, new[] { scenarioStep, examples });
        var feature = Node("WithBackgroundAndExample", GherkinSymbolKinds.Feature, 0, 17, new[] { background, outline });
        return new[] { feature };
    }

    [Fact]
    public void BuildStructureEntries_flattens_containers_and_excludes_steps_and_examples()
    {
        var entries = GherkinNavigationBarLayout.BuildStructureEntries(RuleAndBackgroundTree());

        entries.Should().HaveCount(3);
        entries[0].Name.Should().Be("WithBackgroundAndExample");
        entries[1].Name.Should().Be("we always start from a given first number");
        entries[2].Name.Should().Be("table driven scenarios");
    }

    [Fact]
    public void FindSelectedStructureIndex_finds_the_enclosing_container()
    {
        var entries = GherkinNavigationBarLayout.BuildStructureEntries(RuleAndBackgroundTree());

        var index = GherkinNavigationBarLayout.FindSelectedStructureIndex(entries, caretLine: 9);

        index.Should().Be(2); // "table driven scenarios"
    }

    [Fact]
    public void FindSelectedStructureIndex_returns_minus_one_when_caret_outside_all_containers()
    {
        var entries = GherkinNavigationBarLayout.BuildStructureEntries(SingleScenarioTree());

        var index = GherkinNavigationBarLayout.FindSelectedStructureIndex(entries, caretLine: 100);

        index.Should().Be(-1);
    }
}
