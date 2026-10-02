using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

[Collection("PerfTelemetrySampleEnvVar")]
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

    [Fact]
    public void Default_sample_rate_is_five_percent()
    {
        PerformanceTelemetrySampler.DefaultSampleRate.Should().Be(0.05);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void FromEnvironment_defaults_to_five_percent_when_unset_or_unparsable(string? value)
    {
        // 0.05 is a strict threshold on NextDouble(): 0.049 samples, 0.051 does not.
        WithSampleRateEnv(value, () =>
        {
            Sampler(0.049).ShouldSample().Should().BeTrue();
            Sampler(0.051).ShouldSample().Should().BeFalse();
        });

        static PerformanceTelemetrySampler Sampler(double nextDouble) =>
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(nextDouble));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("-1")]
    public void FromEnvironment_zero_disables_sampling(string value)
    {
        WithSampleRateEnv(value, () =>
        {
            var sut = PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.0));
            for (var i = 0; i < 100; i++)
                sut.ShouldSample().Should().BeFalse();
        });
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.5")]
    public void FromEnvironment_honours_an_explicit_override(string value)
    {
        WithSampleRateEnv(value, () =>
        {
            var rate = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(rate - 0.01)).ShouldSample().Should().BeTrue();
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.99)).ShouldSample()
                .Should().Be(rate >= 1.0);
        });
    }

    private static void WithSampleRateEnv(string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(PerformanceTelemetrySampler.SampleRateEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PerformanceTelemetrySampler.SampleRateEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PerformanceTelemetrySampler.SampleRateEnvVar, original);
        }
    }

    private sealed class StubRandom : Random
    {
        private readonly double _value;
        public StubRandom(double value) => _value = value;
        public override double NextDouble() => _value;
    }
}
