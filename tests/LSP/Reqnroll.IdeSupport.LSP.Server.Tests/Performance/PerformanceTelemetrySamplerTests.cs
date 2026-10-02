using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

[Collection(PerfTelemetrySampleEnvVarCollection.Name)]
/// <summary>Serializes env-var-mutating tests and keeps every other collection from running alongside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfTelemetrySampleEnvVarCollection
{
    public const string Name = "PerfTelemetrySampleEnvVar";
}

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
    [InlineData(null, 0.05, false)]
    [InlineData("", 0.05, false)]
    [InlineData("   ", 0.05, false)]
    [InlineData("on", 0.05, false)]
    [InlineData("TRUE", 0.05, false)]
    [InlineData("Yes", 0.05, false)]
    [InlineData("0", 0.0, false)]
    [InlineData("0.0", 0.0, false)]
    [InlineData("off", 0.0, false)]
    [InlineData("OFF", 0.0, false)]
    [InlineData("false", 0.0, false)]
    [InlineData("no", 0.0, false)]
    [InlineData(" off ", 0.0, false)]
    [InlineData("-1", 0.0, false)]            // finite negative: disabled, as before #862
    [InlineData(" 0.1 ", 0.1, false)]
    [InlineData("1", 1.0, false)]
    [InlineData("1.0", 1.0, false)]
    [InlineData("NaN", 0.05, true)]
    [InlineData("Infinity", 0.05, true)]
    [InlineData("-Infinity", 0.05, true)]
    [InlineData("5", 0.05, true)]
    [InlineData("1e9", 0.05, true)]
    [InlineData("not-a-number", 0.05, true)]
    public void ResolveRate_applies_the_documented_rules(string? raw, double expected, bool expectWarning)
    {
        PerformanceTelemetrySampler.ResolveRate(raw, out var warning).Should().Be(expected);
        (warning is not null).Should().Be(expectWarning);
        if (expectWarning) warning.Should().Contain(PerformanceTelemetrySampler.SampleRateEnvVar);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public void FromEnvironment_defaults_to_five_percent(string? value)
    {
        // 0.05 is a strict threshold on NextDouble(): 0.049 samples, 0.051 does not.
        WithSampleRateEnv(value, () =>
        {
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.049)).ShouldSample().Should().BeTrue();
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.051)).ShouldSample().Should().BeFalse();
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    public void FromEnvironment_disabled_values_never_sample(string value)
    {
        WithSampleRateEnv(value, () =>
        {
            var sut = PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.0));
            for (var i = 0; i < 100; i++)
                sut.ShouldSample().Should().BeFalse();
        });
    }

    [Fact]
    public void FromEnvironment_honours_an_explicit_override()
    {
        WithSampleRateEnv("0.5", () =>
        {
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.49)).ShouldSample().Should().BeTrue();
            PerformanceTelemetrySampler.FromEnvironment(new StubRandom(0.51)).ShouldSample().Should().BeFalse();
        });
    }

    [Fact]
    public void FromEnvironment_logs_one_warning_for_an_invalid_value_and_none_for_a_valid_one()
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        WithSampleRateEnv("1e9", () => PerformanceTelemetrySampler.FromEnvironment(logger: logger));
        logger.Received(1).Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Warning));

        logger.ClearReceivedCalls();
        WithSampleRateEnv("off", () => PerformanceTelemetrySampler.FromEnvironment(logger: logger));
        logger.DidNotReceiveWithAnyArgs().Log(default!);
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
