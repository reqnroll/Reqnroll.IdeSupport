using Reqnroll.IdeSupport.Rider.Backend.Debugger;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

public class BreakpointLineFilterTests
{
    [Theory]
    [InlineData("    Given I have 5 cucumbers")]
    [InlineData("When I eat 2")]
    [InlineData("  Scenario: Eating")]
    [InlineData("    | name | value |")]
    [InlineData("\tThen nothing is left")]
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

    [Fact]
    public void Accepts_a_hash_that_is_not_at_the_start_of_the_line()
    {
        Assert.True(BreakpointLineFilter.IsBreakpointCandidate("    Given I have a #hashtag"));
    }
}
