using Reqnroll.IdeSupport.LSP.Core.Formatting;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Formatting;

public class GherkinTableLocatorTests
{
    private DeveroomGherkinDocument ParseDocument(params string[] lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        var parser = new DeveroomGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());
        parser.ParseAndCollectErrors(text, new IdeSupportNullLogger(), out var gherkinDocument, out _);
        return gherkinDocument;
    }

    // ── FindTableLineRange tests ──────────────────────────────────────────────

    [Fact]
    public void FindTableLineRange_returns_null_when_cursor_is_not_near_a_table()
    {
        var lines = new[]
        {
            "Feature: foo",   // 0
            "Scenario: bar",  // 1
            "    Given step", // 2
            ""                // 3
        };
        GherkinTableLocator.FindTableLineRange(lines, 2).Should().BeNull();
    }

    [Fact]
    public void FindTableLineRange_returns_single_row_range_when_only_one_table_row()
    {
        var lines = new[]
        {
            "Feature: foo",            // 0
            "Scenario: bar",           // 1
            "    Given step",          // 2
            "    | header | value |",  // 3
            ""                         // 4
        };
        GherkinTableLocator.FindTableLineRange(lines, 3).Should().Be((3, 3));
    }

    [Fact]
    public void FindTableLineRange_returns_full_range_for_multi_row_table_cursor_on_first_row()
    {
        var lines = new[]
        {
            "Feature: foo",             // 0
            "Scenario: bar",            // 1
            "    Given step",           // 2
            "    | col1 | col2 |",      // 3
            "    | a | b |",            // 4
            "    | longer | cell |",    // 5
            ""                          // 6
        };
        GherkinTableLocator.FindTableLineRange(lines, 3).Should().Be((3, 5));
    }

    [Fact]
    public void FindTableLineRange_returns_full_range_for_multi_row_table_cursor_on_middle_row()
    {
        var lines = new[]
        {
            "Feature: foo",             // 0
            "Scenario: bar",            // 1
            "    Given step",           // 2
            "    | col1 | col2 |",      // 3
            "    | a | b |",            // 4  ← cursor
            "    | longer | cell |",    // 5
            ""                          // 6
        };
        GherkinTableLocator.FindTableLineRange(lines, 4).Should().Be((3, 5));
    }

    [Fact]
    public void FindTableLineRange_returns_range_when_cursor_is_on_blank_line_after_table()
    {
        // Simulates the \n trigger: cursor is now on the new blank line, table is the line above.
        var lines = new[]
        {
            "Feature: foo",             // 0
            "Scenario: bar",            // 1
            "    Given step",           // 2
            "    | col1 | col2 |",      // 3
            "    | a | b |",            // 4
            "",                         // 5  ← cursor (just pressed Enter after line 4)
            ""                          // 6
        };
        GherkinTableLocator.FindTableLineRange(lines, 5).Should().Be((3, 4));
    }

    [Fact]
    public void FindTableLineRange_returns_null_when_blank_line_is_not_adjacent_to_table()
    {
        var lines = new[]
        {
            "Feature: foo",             // 0
            "Scenario: bar",            // 1
            "    Given step",           // 2  ← cursor is here (blank, but not after table)
            ""                          // 3
        };
        // Line 2 is a step line, line 1 is "Scenario: bar" — neither is a table row
        GherkinTableLocator.FindTableLineRange(lines, 3).Should().BeNull();
    }

    // ── FindTableAtLine tests ─────────────────────────────────────────────────

    [Fact]
    public void FindTableAtLine_returns_DataTable_under_step()
    {
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    | header |",
            "    | value  |",
            ""
        };
        var doc = ParseDocument(lines);

        var result = GherkinTableLocator.FindTableAtLine(doc, 3); // line index 3 = "| header |"

        result.Should().NotBeNull();
        result!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void FindTableAtLine_returns_Examples_table_in_ScenarioOutline()
    {
        var lines = new[]
        {
            "Feature: foo",
            "Scenario Outline: bar",
            "    Given <x>",
            "    Examples:",
            "    | x |",
            "    | 1 |",
            ""
        };
        var doc = ParseDocument(lines);

        var result = GherkinTableLocator.FindTableAtLine(doc, 4); // line index 4 = "| x |"

        result.Should().NotBeNull();
    }

    [Fact]
    public void FindTableAtLine_returns_null_when_no_table_at_line()
    {
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given step",
            ""
        };
        var doc = ParseDocument(lines);

        GherkinTableLocator.FindTableAtLine(doc, 2).Should().BeNull();
    }
}
