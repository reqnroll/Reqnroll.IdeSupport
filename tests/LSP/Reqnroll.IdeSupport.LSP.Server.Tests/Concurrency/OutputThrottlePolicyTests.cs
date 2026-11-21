using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

public class OutputThrottlePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(390)]
    [InlineData(OutputThrottlePolicy.RampStartLines)]
    public void A_document_up_to_the_start_of_the_ramp_is_not_throttled(int lines)
    {
        OutputThrottlePolicy.IntervalFor(lines).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(OutputThrottlePolicy.RampEndLines)]
    [InlineData(18_355)]
    [InlineData(1_000_000)]
    public void A_document_from_the_end_of_the_ramp_upward_gets_the_longest_interval(int lines)
    {
        OutputThrottlePolicy.IntervalFor(lines).Should().Be(TimeSpan.FromMilliseconds(450));
    }

    [Theory]
    [InlineData(4_001, 0)]
    [InlineData(5_000, 75)]
    [InlineData(7_000, 225)]
    [InlineData(9_000, 375)]
    [InlineData(9_999, 450)]
    public void Between_the_ends_of_the_ramp_the_interval_grows_linearly(int lines, int expectedMilliseconds)
    {
        OutputThrottlePolicy.IntervalFor(lines).TotalMilliseconds.Should().Be(expectedMilliseconds);
    }

    [Fact]
    public void The_interval_never_shrinks_as_a_document_grows_and_never_exceeds_the_maximum()
    {
        var previous = TimeSpan.Zero;
        for (var lines = 1; lines <= 12_000; lines++)
        {
            var interval = OutputThrottlePolicy.IntervalFor(lines);
            interval.Should().BeGreaterThanOrEqualTo(previous, $"at {lines} lines");
            interval.Should().BeLessThanOrEqualTo(OutputThrottlePolicy.MaxInterval);
            previous = interval;
        }
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("Feature: X", 1)]
    [InlineData("Feature: X\n", 2)]
    [InlineData("a\r\nb\r\nc", 3)]
    public void Lines_are_counted_by_line_feeds(string text, int expected)
    {
        OutputThrottlePolicy.CountLines(text).Should().Be(expected);
    }

    [Fact]
    public void The_interval_for_a_text_is_the_interval_for_its_line_count()
    {
        var text = string.Join("\n", Enumerable.Repeat("  Given a step", 7_000));

        OutputThrottlePolicy.IntervalFor(text).Should().Be(OutputThrottlePolicy.IntervalFor(7_000));
    }

    [Fact]
    public void A_buffer_that_is_not_open_is_not_throttled()
    {
        var buffers = Substitute.For<IDocumentBufferService>();
        var uri = DocumentUri.FromFileSystemPath("/workspace/closed.feature");
        buffers.TryGet(uri, out Arg.Any<DocumentBuffer?>()).Returns(x => { x[1] = null; return false; });

        buffers.ThrottleInterval(uri).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void An_open_buffer_is_throttled_according_to_its_size()
    {
        var buffers = Substitute.For<IDocumentBufferService>();
        var uri = DocumentUri.FromFileSystemPath("/workspace/big.feature");
        var buffer = new DocumentBuffer(uri, 1, string.Join("\n", Enumerable.Repeat("x", 12_000)));
        buffers.TryGet(uri, out Arg.Any<DocumentBuffer?>()).Returns(x => { x[1] = buffer; return true; });

        buffers.ThrottleInterval(uri).Should().Be(OutputThrottlePolicy.MaxInterval);
    }
}
