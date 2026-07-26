using Reqnroll.IdeSupport.LSP.Core.Formatting;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Formatting;

public class GherkinDocumentFormatterTests
{
    private readonly GherkinFormatSettings _defaultSettings = new();

    private GherkinDocumentFormatter CreateSUT() => new();

    private DeveroomGherkinDocument ParseDocument(params string[] lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        var parser = new DeveroomGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());
        parser.ParseAndCollectErrors(text, new IdeSupportNullLogger(), out var gherkinDocument, out _);
        return gherkinDocument;
    }

    private static DocumentLinesEditBuffer Buffer(params string[] lines)
        => new(lines.ToArray());

    private static string Format(DocumentLinesEditBuffer buffer)
        => buffer.GetModifiedText(Environment.NewLine);

    // ── DocString tests ───────────────────────────────────────────────────────

    [Fact]
    public void Should_not_remove_closing_delimiter_of_an_empty_docstring()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    ```",
            "    ```",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        var expected = string.Join(Environment.NewLine,
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "        ```",
            "        ```",
            "");
        Format(buffer).Should().Be(expected);
    }

    [Fact]
    public void Should_not_remove_empty_line_from_a_single_empty_line_docstring()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    ```",
            "    ",
            "    ```",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        var expected = string.Join(Environment.NewLine,
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "        ```",
            "        ",
            "        ```",
            "");
        Format(buffer).Should().Be(expected);
    }

    [Fact]
    public void Should_not_remove_whitespace_from_a_single_whitespace_line_docstring()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    ```",
            "     ",
            "    ```",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        var expected = string.Join(Environment.NewLine,
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "        ```",
            "         ",
            "        ```",
            "");
        Format(buffer).Should().Be(expected);
    }

    // ── Table tests ───────────────────────────────────────────────────────────

    [Fact]
    public void Should_not_crash_on_table_with_rows_missing_closing_pipe()
    {
        // Rows without a closing | are valid in an in-progress feature file.
        // The formatter must not crash, and well-formed rows must be correctly padded.
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "Given table",
            "    | header1 | header2 |",
            "    | value1  | value2  |",
            "    | short   | a much longer value |",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        // All three rows are complete — widths align across all rows.
        // header1 = 7, header2 = 20 (from "a much longer value")
        buffer.GetLineOneBased(4).Should().Be("        | header1 | header2             |");
        buffer.GetLineOneBased(5).Should().Be("        | value1  | value2              |");
        buffer.GetLineOneBased(6).Should().Be("        | short   | a much longer value |");
    }

    // ── Table cell alignment ──────────────────────────────────────────────────

    [Theory]
    // Header is 13 chars, so all cells are padded to width 13
    [InlineData("| 123 |", true, "|         123 |")]   // Right-align: only digits
    [InlineData("| -12.3 |", true, "|       -12.3 |")] // Right-align: fraction
    [InlineData("| 1,000 |", true, "|       1,000 |")] // Right-align: thousand separator
    [InlineData("| -1.23E8 |", true, "|     -1.23E8 |")] // Right-align: scientific
    [InlineData("| abc123 |", true, "| abc123      |")] // Left-align: mixed
    [InlineData("| 12abc |", true, "| 12abc       |")]  // Left-align: digits at start
    [InlineData("| abc |", true, "| abc         |")]    // Left-align: only letters
    [InlineData("| !@#4$% |", true, "| !@#4$%      |")] // Left-align: special + digit
    [InlineData("| !@#$% |", true, "| !@#$%       |")] // Left-align: only special
    [InlineData("| 123 |", false, "| 123         |")]   // Left-align: right-align disabled
    [InlineData("| abc123 |", false, "| abc123      |")] // Left-align: mixed, disabled
    public void Should_align_table_cells_based_on_content_and_setting(
        string tableRow, bool rightAlign, string expectedCell)
    {
        var sut = CreateSUT();
        var settings = new GherkinFormatSettings();
        settings.Configuration.TableCellRightAlignNumericContent = rightAlign;
        settings.Configuration.TableCellPaddingSize = 1;
        settings.Indent = "    ";

        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    | HeaderValue |",  // header row sets width to 13
            $"    {tableRow}",
            ""
        };
        var buffer = Buffer(lines);
        sut.FormatGherkinDocument(ParseDocument(lines), buffer, settings);
        var formattedLine = buffer.GetLineOneBased(5).TrimEnd().TrimStart(); // data row is line 5
        formattedLine.Should().Be(expectedCell);
    }

    // ── FormatTable: unfinished cell handling ─────────────────────────────────

    [Fact]
    public void FormatTable_adds_trailing_pipe_and_pads_unfinished_cell()
    {
        // Row missing trailing | should have it added, padded to the column width.
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: foo",
            "Scenario: bar",
            "    Given table",
            "    | col1  | col2         |",
            "    | short | longer value",  // missing trailing |
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        buffer.GetLineOneBased(5).TrimStart().Should().Be("| short | longer value |");
    }

    // ── Indentation tests ─────────────────────────────────────────────────────

    [Fact]
    public void Should_fix_misindented_steps()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: Addition",
            "Scenario: Add",
            "Given I have 50",
            "When I add",
            "Then result is 50",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        var expected = string.Join(Environment.NewLine,
            "Feature: Addition",
            "Scenario: Add",
            "    Given I have 50",
            "    When I add",
            "    Then result is 50",
            "");
        Format(buffer).Should().Be(expected);
    }

    [Fact]
    public void Should_replace_repeated_step_keywords_with_And()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "Feature: Addition",
            "Scenario: Add two numbers",
            "    Given I have entered 50 into the calculator",
            "    Given I have entered 70 into the calculator",
            "    When I add them",
            "    When I check the result",
            "    Then there should be no error",
            "    Then the result should be 120",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        var expected = string.Join(Environment.NewLine,
            "Feature: Addition",
            "Scenario: Add two numbers",
            "    Given I have entered 50 into the calculator",
            "    And I have entered 70 into the calculator",
            "    When I add them",
            "    And I check the result",
            "    Then there should be no error",
            "    And the result should be 120",
            "");
        Format(buffer).Should().Be(expected);
    }

    [Fact]
    public void Should_normalise_tag_whitespace()
    {
        var sut = CreateSUT();
        var lines = new[]
        {
            "  @tag1    @tag2",
            "Feature: foo",
            "  @tag3",
            "Scenario: bar",
            "    Given step",
            ""
        };
        var buffer = Buffer(lines);

        sut.FormatGherkinDocument(ParseDocument(lines), buffer, _defaultSettings);

        buffer.GetLineOneBased(1).Should().Be("@tag1 @tag2");
        buffer.GetLineOneBased(3).Should().Be("@tag3");
    }
}
