using Reqnroll.IdeSupport.Rider.Backend.Debugger;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

public class BreakpointLineFilterTests
{
    [Theory]
    [InlineData("    Given I have 5 cucumbers")]
    [InlineData("When I eat 2")]
    [InlineData("  Scenario: Eating")]
    [InlineData("\tThen nothing is left")]
    [InlineData("    Given the client added <num> pcs of \"<item>\" to the basket")]
    public void Accepts_lines_with_content(string line)
    {
        Assert.True(BreakpointLineFilter.IsBreakpointCandidate(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Rejects_blank_lines(string line)
    {
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(line));
    }

    [Theory]
    [InlineData("# a comment")]
    [InlineData("    # indented comment")]
    [InlineData("#language: en")]
    public void Rejects_comment_lines(string line)
    {
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(line));
    }

    [Theory]
    [InlineData("@b")]
    [InlineData("@a @issue:555")]
    [InlineData("    @smoke")]
    public void Rejects_tag_lines(string line)
    {
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(line));
    }

    [Theory]
    [InlineData("    | name | value |")]
    [InlineData("        | Electric guitar |        1 |")]
    [InlineData("|1|")]
    public void Rejects_table_rows_including_examples_rows(string line)
    {
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(line));
    }

    [Fact]
    public void Accepts_a_hash_or_at_sign_that_is_not_at_the_start_of_the_line()
    {
        Assert.True(BreakpointLineFilter.IsBreakpointCandidate("    Given I have a #hashtag"));
        Assert.True(BreakpointLineFilter.IsBreakpointCandidate("    Given I mail a@b.c"));
    }

    [Theory]
    [InlineData(0, true)]   // Scenario:
    [InlineData(1, true)]   // Given ... (the step owning the doc string)
    [InlineData(2, false)]  // opening """
    [InlineData(3, false)]  // content
    [InlineData(4, false)]  // content that looks like a step
    [InlineData(5, false)]  // closing """
    [InlineData(6, true)]   // When ...
    [InlineData(7, false)]  // opening ```
    [InlineData(8, false)]  // """ inside a ``` doc string does not close it
    [InlineData(9, false)]  // closing ```
    [InlineData(10, true)]  // Then ...
    public void Rejects_doc_strings_and_their_delimiters(int line, bool expected)
    {
        var lines = new[]
        {
            "Scenario: Doc strings",
            "    Given a document",
            "        \"\"\"",
            "        Some text",
            "        Given not a step",
            "        \"\"\"",
            "    When it is read",
            "        ```json",
            "        \"\"\"",
            "        ```",
            "    Then it is fine",
        };

        Assert.Equal(expected, BreakpointLineFilter.IsBreakpointCandidate(i => lines[i], lines.Length, line));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Rejects_lines_outside_the_document(int line)
    {
        var lines = new[] { "Feature: x", "Scenario: y", "    Given z" };
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(i => lines[i], lines.Length, line));
    }

    [Fact]
    public void An_unclosed_doc_string_runs_to_the_end_of_the_document()
    {
        var lines = new[] { "    Given a document", "        \"\"\"", "    When it is read" };
        Assert.False(BreakpointLineFilter.IsBreakpointCandidate(i => lines[i], lines.Length, 2));
    }
}
