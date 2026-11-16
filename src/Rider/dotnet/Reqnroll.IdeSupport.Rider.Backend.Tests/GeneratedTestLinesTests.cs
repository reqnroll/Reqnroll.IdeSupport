using System;
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

    [Fact]
    public void MsTest_row_attributes_give_their_pickle_indexes_in_order()
    {
        Assert.Equal(new[] { 3, 4 }, GeneratedTestLines.PickleIndexesOfRowAttributes(MsTestOutline));
    }

    [Theory]
    [InlineData("[global::NUnit.Framework.TestCaseAttribute(\"50\", \"70\", \"120\", \"0\", null)]", 0)]
    [InlineData("[global::NUnit.Framework.TestCaseAttribute(\"a\", \"7\", null, Category=\"x\", TestName=\"T(a)\")]", 7)]
    [InlineData("[global::Xunit.InlineDataAttribute(\"1\", \"Guitar pick\", \"12\", new string[0])]", 12)]
    [InlineData("[InlineData(\"x,y\", \"(\", \"2\", new string[] { \"a\", \"b\" })]", 2)]
    [InlineData("[DataRow(@\"a \"\"quoted\"\" value\", \"9\", null)]", 9)]
    public void Row_attribute_pickle_index_is_the_second_to_last_positional_argument(string attribute, int expected)
    {
        Assert.Equal(expected, GeneratedTestLines.PickleIndexOfRowAttribute(attribute));
    }

    [Theory]
    [InlineData("[global::Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute(\"Title\")]")]
    [InlineData("[TestCaseSource(\"Rows\", \"1\", null)]")]
    [InlineData("[DataRow(\"1\", \"not a number\", null)]")]
    [InlineData("[DataRow(\"1\")]")]
    [InlineData("[DataRow(\"1\", \"2\", null")]
    [InlineData("")]
    public void Other_attributes_have_no_pickle_index(string attribute)
    {
        Assert.Null(GeneratedTestLines.PickleIndexOfRowAttribute(attribute));
    }

    [Theory]
    [InlineData("Different amounts calc correctly(1,Guitar pick,1.50,0)", 0)]
    [InlineData("Different amounts calc correctly(2,Guitar, pick,3,17)", 17)]
    [InlineData("Only a pickle(5)", 5)]
    public void MsTest_display_name_ends_with_the_pickle_index(string displayName, int expected)
    {
        Assert.Equal(expected, GeneratedTestLines.PickleIndexOfDisplayName(displayName));
    }

    [Theory]
    [InlineData("DifferentAmountsCalcCorrectly")]
    [InlineData("Title(1,Guitar pick,x)")]
    [InlineData("Title(1,2")]
    [InlineData(null)]
    public void Other_names_have_no_pickle_index(string displayName)
    {
        Assert.Null(GeneratedTestLines.PickleIndexOfDisplayName(displayName));
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

    [Fact]
    public void Row_line_is_found_by_position_among_the_outline_pickle_indexes()
    {
        var rows = new[] { 10, 12, 18 };
        var indexes = new[] { 5, 6, 7 };

        Assert.Equal(10, GeneratedTestLines.RowLine(5, indexes, rows));
        Assert.Equal(18, GeneratedTestLines.RowLine(7, indexes, rows));
        Assert.Equal(-1, GeneratedTestLines.RowLine(8, indexes, rows));
        Assert.Equal(-1, GeneratedTestLines.RowLine(4, indexes, rows));
        Assert.Equal(-1, GeneratedTestLines.RowLine(5, Array.Empty<int>(), rows));
    }
}
