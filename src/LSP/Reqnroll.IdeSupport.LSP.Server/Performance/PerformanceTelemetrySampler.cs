using System.Globalization;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Probabilistic sampler. The rate is read from the <c>REQNROLL_PERF_TELEMETRY_SAMPLE</c>
/// environment variable (a fraction in <c>[0,1]</c>); when unset or invalid it defaults to
/// <see cref="DefaultSampleRate"/> = 0.05, i.e. perf telemetry is <b>on by default</b> at ~5% of
/// samples (durations only, no paths). Set the variable to <c>0</c> (or <c>off</c>/<c>false</c>/<c>no</c>) to disable sampling, or to
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

    /// <summary>
    /// Creates a sampler from <see cref="SampleRateEnvVar"/> (parsed by <see cref="ResolveRate"/>), logging a
    /// Warning via <paramref name="logger"/> when the value is invalid and the default is used instead.
    /// </summary>
    /// <param name="random">
    /// Optional RNG (a test seam). It is called concurrently from every handler thread, so the instance
    /// <b>must be thread-safe</b>; the default is <see cref="Random.Shared"/>.
    /// </param>
    /// <param name="logger">Optional logger for the invalid-value warning.</param>
    public static PerformanceTelemetrySampler FromEnvironment(Random? random = null, IIdeSupportLogger? logger = null)
    {
        var raw = Environment.GetEnvironmentVariable(SampleRateEnvVar);
        var rate = ResolveRate(raw, out var warning);
        if (warning is not null) logger?.LogWarning(warning);
        return new PerformanceTelemetrySampler(rate, random);
    }

    /// <summary>
    /// Resolves the effective sample rate from a raw env-var value (trimmed, case-insensitive):
    /// unset/empty/whitespace and <c>on</c>/<c>true</c>/<c>yes</c> give <see cref="DefaultSampleRate"/>;
    /// <c>off</c>/<c>false</c>/<c>no</c> and a finite negative number disable sampling (0, preserving the
    /// pre-#862 meaning of "disabled"); a number in <c>[0,1]</c> is used as-is; anything else (not a
    /// number, NaN, +/-Infinity, or greater than 1) is <b>invalid</b> and falls back to the default with
    /// <paramref name="warning"/> set.
    /// </summary>
    public static double ResolveRate(string? raw, out string? warning)
    {
        warning = null;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return DefaultSampleRate;

        switch (value.ToLowerInvariant())
        {
            case "off" or "false" or "no": return 0.0;
            case "on" or "true" or "yes": return DefaultSampleRate;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            && !double.IsNaN(r) && !double.IsInfinity(r) && r <= 1.0)
            return Math.Max(r, 0.0);

        warning = $"Ignoring invalid {SampleRateEnvVar} value '{value}' (expected a fraction in [0,1], or off/on); using the default rate {DefaultSampleRate.ToString(CultureInfo.InvariantCulture)}.";
        return DefaultSampleRate;
    }
}
