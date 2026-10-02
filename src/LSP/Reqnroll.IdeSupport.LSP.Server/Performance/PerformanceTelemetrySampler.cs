using System.Globalization;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Probabilistic sampler. The rate is read from the <c>REQNROLL_PERF_TELEMETRY_SAMPLE</c>
/// environment variable (a fraction in <c>[0,1]</c>); when unset or unparsable it defaults to
/// <see cref="DefaultSampleRate"/> = 0.05, i.e. perf telemetry is <b>on by default</b> at ~5% of
/// samples (durations only, no paths). Set the variable to <c>0</c> to disable sampling, or to
/// another fraction to override. The <c>REQNROLL_TELEMETRY_ENABLED</c> kill switch and each
/// host's own opt-out are enforced host-side, downstream of this sampler.
/// </summary>
public sealed class PerformanceTelemetrySampler : IPerformanceTelemetrySampler
{
    /// <summary>Name of the environment variable that configures the sampling rate (a fraction in <c>[0,1]</c>).</summary>
    public const string SampleRateEnvVar = "REQNROLL_PERF_TELEMETRY_SAMPLE";

    /// <summary>On by default: ~5% of perf samples are emitted unless <see cref="SampleRateEnvVar"/> overrides it (<c>0</c> disables).</summary>
    public const double DefaultSampleRate = 0.05;

    private readonly double _rate;
    private readonly Random _random;

    /// <summary>Initializes a new instance of the <see cref="PerformanceTelemetrySampler"/> class.</summary>
    public PerformanceTelemetrySampler(double rate, Random? random = null)
    {
        _rate = Math.Clamp(rate, 0.0, 1.0);
        _random = random ?? Random.Shared;
    }

    /// <summary>Randomly decides, based on the configured rate, whether the current sample should be emitted.</summary>
    public bool ShouldSample()
    {
        if (_rate <= 0.0) return false;
        if (_rate >= 1.0) return true;
        return _random.NextDouble() < _rate;
    }

    /// <summary>Creates a sampler using the rate from <see cref="SampleRateEnvVar"/>, falling back to <see cref="DefaultSampleRate"/> when unset or unparsable.</summary>
    public static PerformanceTelemetrySampler FromEnvironment(Random? random = null)
    {
        var raw = Environment.GetEnvironmentVariable(SampleRateEnvVar);
        var rate = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            ? r
            : DefaultSampleRate;
        return new PerformanceTelemetrySampler(rate, random);
    }
}
