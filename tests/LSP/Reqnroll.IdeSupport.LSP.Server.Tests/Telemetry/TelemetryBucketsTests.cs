#nullable enable

using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class TelemetryBucketsTests
{
    [Theory]
    [InlineData(0, "<=10")]
    [InlineData(10, "<=10")]
    [InlineData(11, "<=25")]
    [InlineData(100, "<=100")]
    [InlineData(101, "<=250")]
    [InlineData(1000, "<=1000")]
    [InlineData(5000, "<=5000")]
    [InlineData(5001, ">5000")]
    [InlineData(600000, ">5000")]
    public void Duration_uses_the_PerfSample_bucket_scheme(int milliseconds, string expected) =>
        TelemetryBuckets.Duration(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);

    [Theory]
    [InlineData(-3, "0")]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(2, "2-10")]
    [InlineData(10, "2-10")]
    [InlineData(11, "11-50")]
    [InlineData(50, "11-50")]
    [InlineData(51, "51-200")]
    [InlineData(200, "51-200")]
    [InlineData(201, "201-1000")]
    [InlineData(1000, "201-1000")]
    [InlineData(1001, "1000+")]
    public void LineCount_maps_to_a_closed_set_of_buckets(int lines, string expected) =>
        TelemetryBuckets.LineCount(lines).Should().Be(expected);

    [Fact]
    public void DurationSince_returns_a_bucket_from_the_shared_scheme() =>
        TelemetryBuckets.DurationSince(System.Diagnostics.Stopwatch.GetTimestamp()).Should().MatchRegex(@"^(<=\d+|>5000)$");
}
