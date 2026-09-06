namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class DurationFormatterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(42.4, 42)]
    [InlineData(42.5, 43)] // half away from zero, not banker's rounding
    [InlineData(42.6, 43)]
    [InlineData(999.99, 1000)]
    public void RoundMilliseconds_rounds_half_away_from_zero(double elapsedMs, long expected)
        => DurationFormatter.RoundMilliseconds(elapsedMs).Should().Be(expected);

    [Fact]
    public void FormatMilliseconds_from_a_double_appends_the_ms_suffix()
        => DurationFormatter.FormatMilliseconds(42.5).Should().Be("43ms");

    [Fact]
    public void FormatMilliseconds_from_a_TimeSpan_matches_the_double_overload()
        => DurationFormatter.FormatMilliseconds(TimeSpan.FromMilliseconds(1500))
            .Should().Be(DurationFormatter.FormatMilliseconds(1500d));
}
