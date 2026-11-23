using System;
using System.Linq;
using Reqnroll.IdeSupport.Rider.Backend.UnitTesting;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

public class GeneratedTestLinesTests
{
    private static (Func<int, string> getLine, int count) Doc(params string[] lines) => (i => lines[i], lines.Length);

    [Fact]
    public void Feature_line_skips_language_header_comments_tags_and_blanks()
    {
        var (getLine, count) = Doc(
            "﻿# language: fr",
            "",
            "# a comment",
            "@tag1 @tag2",
            "Fonctionnalité: Panier",
            "  Scénario: Un article");

        Assert.Equal(4, GeneratedTestLines.FeatureLine(getLine, count));
    }

    [Fact]
    public void Feature_line_is_minus_one_for_a_file_without_content()
    {
        var (getLine, count) = Doc("", "# only a comment", "@tag");
        Assert.Equal(-1, GeneratedTestLines.FeatureLine(getLine, count));
    }

    private const string MsTestOutline = """
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute("Different amounts calc correctly")]
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.TestPropertyAttribute("FeatureTitle", "shopping basket with examples table")]
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute("1", "Guitar pick", "1.50", "3", null, DisplayName="Different amounts calc correctly(1,Guitar pick,1.50,3)")]
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute("2", "Guitar, \"pick\"", "3", "4", new string[] {"slow"}, DisplayName="Different amounts calc correctly(2,Guitar pick,3,4)")]
                public async global::System.Threading.Tasks.Task DifferentAmountsCalcCorrectly(string num, string item, string amount, string @__pickleIndex, string[] exampleTags)
                {
                    string pickleIndex = @__pickleIndex;
        #line 3
        this.ScenarioInitialize(scenarioInfo, ruleInfo);
        #line hidden
        #line 4
            await testRunner.GivenAsync("the client started shopping", null, null, "Given ");
        #line hidden
        #line 7
            await testRunner.ThenAsync("the price should be $<amount>", null, null, "Then ");
        #line hidden
                }
        """;

    [Fact]
    public void Line_directives_are_zero_based_and_skip_hidden()
    {
        Assert.Equal(new[] { 2, 3, 6 }, GeneratedTestLines.LineDirectives(MsTestOutline));
    }

    [Fact]
    public void Scenario_line_is_the_first_directive_and_last_step_the_highest_after_it()
    {
        Assert.Equal(2, GeneratedTestLines.ScenarioLine(MsTestOutline));
        Assert.Equal(6, GeneratedTestLines.LastStepLine(MsTestOutline, 2));
    }

    [Fact]
    public void Scenario_without_directives_maps_nowhere()
    {
        Assert.Equal(-1, GeneratedTestLines.ScenarioLine("public void Foo() { }"));
        Assert.Equal(5, GeneratedTestLines.LastStepLine("public void Foo() { }", 5));
    }

    // A generated MSTest outline as Reqnroll writes it once its row attributes no longer carry the internal row index:
    // only the example values (the last one numeric) and the tags.
    private const string MsTestOutlineWithoutRowIndex = """
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute("1", "Guitar pick", "3", null, DisplayName="Amounts(1,Guitar pick,3)")]
                [global::Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute("2", "Guitar pick", "0", null, DisplayName="Amounts(2,Guitar pick,0)")]
                public async global::System.Threading.Tasks.Task Amounts(string num, string item, string amount, string[] exampleTags)
                {
                }
        """;

    private const string NUnitOutline = """
                [global::NUnit.Framework.TestCaseAttribute("50", "70", null, Category="x")]
                [global::NUnit.Framework.TestCaseAttribute("50", "70", null, Category="x")]
                [global::NUnit.Framework.TestCaseAttribute("x,y", "(", new string[] { "a", "b" })]
                public async global::System.Threading.Tasks.Task Add(string first, string second, string[] exampleTags)
                {
                }
        """;

    [Fact]
    public void Row_attributes_are_listed_in_declaration_order_with_their_display_names()
    {
        var rows = GeneratedTestLines.RowAttributes(MsTestOutline);

        Assert.Equal(
            new[] { "Different amounts calc correctly(1,Guitar pick,1.50,3)", "Different amounts calc correctly(2,Guitar pick,3,4)" },
            rows.Select(row => row.DisplayName));
        Assert.True(rows[0].End <= rows[1].Start);
    }

    [Fact]
    public void Attributes_that_are_not_rows_or_are_unterminated_are_not_listed()
    {
        Assert.Empty(GeneratedTestLines.RowAttributes("[TestMethod(\"Title\")] [TestCaseSource(\"Rows\")] [DataRow(\"1\", \"2\""));
        Assert.Empty(GeneratedTestLines.RowAttributes(null));
    }

    [Theory]
    [InlineData("Different amounts calc correctly(1,Guitar pick,1.50,3)", 0)]
    [InlineData("Different amounts calc correctly(2,Guitar pick,3,4)", 1)]
    [InlineData("  Different amounts calc correctly(2,Guitar pick,3,4) ", 1)]
    [InlineData("Different amounts calc correctly(3,Guitar pick,3,5)", -1)]
    [InlineData(null, -1)]
    public void An_MsTest_row_is_found_by_its_display_name(string displayName, int expected)
    {
        var rows = GeneratedTestLines.RowAttributes(MsTestOutline);

        Assert.Equal(expected, GeneratedTestLines.RowPosition(rows, rowOffset: null, rowText: null, displayName));
    }

    [Theory]
    [InlineData("Amounts(1,Guitar pick,3)", 0)]
    [InlineData("Amounts(2,Guitar pick,0)", 1)]
    public void Rows_without_the_internal_row_index_are_found_by_position_not_by_a_value(string displayName, int expected)
    {
        // The last example value is numeric; it must not be taken for a row number.
        var rows = GeneratedTestLines.RowAttributes(MsTestOutlineWithoutRowIndex);

        Assert.Equal(expected, GeneratedTestLines.RowPosition(rows, null, null, displayName));
    }

    [Fact]
    public void A_row_located_at_its_own_attribute_is_found_by_where_it_is_even_among_identical_rows()
    {
        var rows = GeneratedTestLines.RowAttributes(NUnitOutline);
        var second = NUnitOutline.IndexOf("[global::NUnit.Framework.TestCaseAttribute", NUnitOutline.IndexOf("TestCaseAttribute", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        var secondText = NUnitOutline.Substring(second, NUnitOutline.IndexOf(']', second) + 1 - second);
        var third = NUnitOutline.LastIndexOf("[global::NUnit.Framework.TestCaseAttribute", StringComparison.Ordinal);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, GeneratedTestLines.RowPosition(rows, second, secondText, "Add(\"50\",\"70\",null)"));
        Assert.Equal(2, GeneratedTestLines.RowPosition(rows, third, NUnitOutline.Substring(third), null));
    }

    [Fact]
    public void A_row_located_at_the_whole_method_is_found_by_its_display_name_instead()
    {
        var rows = GeneratedTestLines.RowAttributes(MsTestOutline);

        Assert.Equal(1, GeneratedTestLines.RowPosition(rows, 0, MsTestOutline, "Different amounts calc correctly(2,Guitar pick,3,4)"));
    }

    [Fact]
    public void Display_names_are_read_from_regular_and_verbatim_string_literals()
    {
        var rows = GeneratedTestLines.RowAttributes(
            "[DataRow(\"a\", null, DisplayName = \"T(\\\"a\\\", b\\\\c)\")] [DataRow(\"b\", null, DisplayName=@\"T(\"\"b\"\")\")]");

        Assert.Equal(new[] { "T(\"a\", b\\c)", "T(\"b\")" }, rows.Select(row => row.DisplayName));
    }

    [Fact]
    public void A_row_position_maps_to_the_example_row_line_at_that_position()
    {
        var rowLines = new[] { 10, 12, 18 };

        Assert.Equal(10, GeneratedTestLines.RowLine(0, rowLines));
        Assert.Equal(18, GeneratedTestLines.RowLine(2, rowLines));
        Assert.Equal(-1, GeneratedTestLines.RowLine(3, rowLines));
        Assert.Equal(-1, GeneratedTestLines.RowLine(-1, rowLines));
    }

    [Fact]
    public void Example_rows_are_the_body_rows_of_every_examples_table_after_the_last_step()
    {
        var (getLine, count) = Doc(
            "Feature: Basket",                        // 0
            "",                                       // 1
            "Scenario Outline: Amounts",              // 2
            "  Given the client started shopping",    // 3
            "  And these items:",                     // 4  last step
            "    | name | price |",                   // 5  its data table
            "    | pick | 1     |",                   // 6
            "",                                       // 7
            "  Examples: cheap",                      // 8
            "    | num | amount |",                   // 9  header
            "    | 1   | 1.50   |",                   // 10
            "    # a comment inside the table",       // 11
            "    | 2   | 3      |",                   // 12
            "",                                       // 13
            "  @expensive",                           // 14
            "  Examples: dear",                       // 15
            "    Some description",                   // 16
            "    | num | amount |",                   // 17 header
            "    | 9   | 90     |",                   // 18
            "",                                       // 19
            "Rule: Later",                            // 20
            "  Background:",                          // 21
            "    Given a table",                      // 22
            "      | a |",                            // 23
            "      | b |");                           // 24

        Assert.Equal(new[] { 10, 12, 18 }, GeneratedTestLines.ExampleRowLines(getLine, count, lastStepLine: 4, maxRows: 3));
    }

    [Fact]
    public void Example_rows_skip_the_last_step_doc_string()
    {
        var (getLine, count) = Doc(
            "Scenario Outline: Text",  // 0
            "  Given this text:",       // 1
            "    \"\"\"",              // 2
            "    | not a row |",        // 3
            "    \"\"\"",              // 4
            "  Examples:",              // 5
            "    | x |",                // 6
            "    | 1 |");               // 7

        Assert.Equal(new[] { 7 }, GeneratedTestLines.ExampleRowLines(getLine, count, lastStepLine: 1, maxRows: 5));
    }

    [Fact]
    public void Example_rows_of_an_outline_without_steps_start_at_the_outline()
    {
        var (getLine, count) = Doc(
            "Scenario Outline: Empty", // 0
            "  Examples:",             // 1
            "    | x |",               // 2
            "    | 1 |",               // 3
            "    | 2 |");              // 4

        Assert.Equal(new[] { 3, 4 }, GeneratedTestLines.ExampleRowLines(getLine, count, lastStepLine: 0, maxRows: 2));
    }
}
