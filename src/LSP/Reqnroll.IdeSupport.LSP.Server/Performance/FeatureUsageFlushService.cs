using System.Globalization;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Default <see cref="IFeatureUsageFlushService"/>. The interval is read from
/// <see cref="FlushIntervalEnvVar"/> (seconds); unset or non-positive disables the periodic
/// flush entirely — counting stays in-memory only and no <c>FeatureUsageSummary</c> event is ever
/// sent, matching <see cref="PerformanceTelemetrySampler"/>'s opt-in-by-default posture (issue
/// #582's "Rollout and rollback": counting itself is a few <c>Interlocked</c> adds and can stay
/// always-on; the increment is inert without this service running).
/// </summary>
/// <remarks>
/// <para>
/// Accepted loss profile: the worst case is one flush window plus the un-flushed tail, lost when
/// the process dies abruptly (force-quit, IDE crash, OS shutdown). <see cref="FlushFinalAsync"/>
/// covers the graceful-shutdown case only. This loss is deliberately not recovered across
/// restarts here — see the issue's "Option D" (disk-backed counters) for why that is out of scope.
/// </para>
/// <para>
/// Privacy: only the allowlisted operation labels and their integer counts ever reach the event
/// (via <see cref="FeatureUsageOperations"/> at the increment site) — no <c>DocumentUri</c>, no
/// free-form detail, matching <see cref="OperationDurationRecorder"/>'s own <c>PerfSample</c>
/// privacy posture.
/// </para>
/// </remarks>
public sealed class FeatureUsageFlushService : IFeatureUsageFlushService
{
    /// <summary>Name of the environment variable that configures the flush interval, in seconds. Unset or non-positive disables periodic flushing.</summary>
    public const string FlushIntervalEnvVar = "REQNROLL_FEATURE_USAGE_FLUSH_INTERVAL_SECONDS";

    /// <summary>Telemetry event name. Must be added to the public telemetry inventory.</summary>
    public const string FeatureUsageSummaryEventName = "FeatureUsageSummary";

    private readonly IFeatureUsageCounters _counters;
    private readonly ClientIdeContext _ide;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetry;
    private readonly TimeSpan? _interval;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Initializes a new instance of the <see cref="FeatureUsageFlushService"/> class.</summary>
    public FeatureUsageFlushService(
        IFeatureUsageCounters counters,
        ClientIdeContext ide,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetry = null,
        TimeSpan? interval = null)
    {
        _counters = counters;
        _ide = ide;
        _logger = logger;
        _telemetry = telemetry;
        _interval = interval ?? IntervalFromEnvironment();
    }

    private static TimeSpan? IntervalFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(FlushIntervalEnvVar);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_interval is not { } interval)
        {
            _logger.LogVerbose(
                $"FeatureUsageFlushService: disabled ({FlushIntervalEnvVar} unset or non-positive) -- usage counting stays in-memory only.");
            return;
        }

        _logger.LogInfo($"FeatureUsageFlushService: flushing every {interval.TotalSeconds:F0}s.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                Flush(isFinal: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path -- the caller performs its own FlushFinalAsync separately.
        }
    }

    /// <inheritdoc/>
    public Task FlushFinalAsync()
    {
        Flush(isFinal: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drains the counters and emits one event, unless the drain is empty -- idle sessions (most
    /// sessions, most of the time) stay completely silent rather than sending an all-zero
    /// heartbeat, which would be pure noise at the ingestion end.
    /// </summary>
    private void Flush(bool isFinal)
    {
        var counts = _counters.Drain();

        // The actual elapsed time since the last flush, not the configured interval -- a final
        // flush can land mid-interval, and this keeps WindowSeconds accurate either way.
        var windowSeconds = Stopwatch.GetElapsedTime(
            Interlocked.Exchange(ref _windowStartTimestamp, Stopwatch.GetTimestamp())).TotalSeconds;

        if (counts.Count == 0)
            return;

        _telemetry?.SendEvent(FeatureUsageSummaryEventName, new Dictionary<string, object?>
        {
            ["Counts"] = counts,
            ["WindowSeconds"] = Math.Round(windowSeconds),
            ["IsFinal"] = isFinal,
            ["IDEClient"] = _ide.Ide,
        });
    }
}
