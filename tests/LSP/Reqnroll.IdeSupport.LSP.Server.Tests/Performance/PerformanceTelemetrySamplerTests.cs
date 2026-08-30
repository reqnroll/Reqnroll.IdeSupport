using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

public class PerfTelemetrySamplerTests
{
    [Fact]
    public void Rate_zero_never_samples()
    {
        var sut = new PerformanceTelemetrySampler(0.0);
        for (var i = 0; i < 100; i++)
            sut.ShouldSample().Should().BeFalse();
    }

    [Fact]
    public void Rate_one_always_samples()
    {
        var sut = new PerformanceTelemetrySampler(1.0);
        for (var i = 0; i < 100; i++)
            sut.ShouldSample().Should().BeTrue();
    }

    [Fact]
    public void Rate_is_clamped_into_unit_interval()
    {
        new PerformanceTelemetrySampler(5.0).ShouldSample().Should().BeTrue();   // clamped to 1
        new PerformanceTelemetrySampler(-5.0).ShouldSample().Should().BeFalse(); // clamped to 0
    }

    [Fact]
    public void Fractional_rate_uses_the_supplied_rng_threshold()
    {
        // Random stub returning a fixed NextDouble lets us assert the < rate comparison deterministically.
        var below = new PerformanceTelemetrySampler(0.5, new StubRandom(0.49));
        var above = new PerformanceTelemetrySampler(0.5, new StubRandom(0.51));

        below.ShouldSample().Should().BeTrue();
        above.ShouldSample().Should().BeFalse();
    }

    private sealed class StubRandom : Random
    {
        private readonly double _value;
        public StubRandom(double value) => _value = value;
        public override double NextDouble() => _value;
    }
}
