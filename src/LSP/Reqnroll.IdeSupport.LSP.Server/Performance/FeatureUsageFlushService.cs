using System.Globalization;
using System.Text.Json;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Default <see cref="IFeatureUsageFlushService"/>. Whether and how often it flushes is resolved by
/// <see cref="ResolveInterval"/> from <see cref="FlushIntervalEnvVar"/> (seconds) and the
/// <see cref="EnabledByDefault"/>/<see cref="DefaultInterval"/> constants (on, every 10 minutes, unless
/// overridden); a <c>null</c> interval disables the periodic flush entirely — counting stays in-memory only and no
/// <c>FeatureUsageSummary</c> event is ever sent (issue #582's "Rollout and rollback": counting
/// itself is a few <c>Interlocked</c> adds and stays always-on).
/// </summary>
/// <remarks>
/// <para>
/// Accepted loss profile: the worst case is one flush window plus the un-flushed tail, lost when
/// the process dies abruptly (force-quit, IDE crash, OS shutdown). <see cref="FlushFinalAsync"/>
/// covers the graceful-shutdown case only (it is driven from the LSP <c>shutdown</c> request, while
/// the transport is still open). This loss is deliberately not recovered across restarts here —
/// see the issue's "Option D" (disk-backed counters). It is, however, <i>detectable</i>: each
/// emitted event carries a monotonically increasing <c>Sequence</c> within its <c>SessionId</c>, so
/// a gap in the sequence marks a lost flush.
/// </para>
/// <para>
/// Identity (<c>IdeClient</c>, <c>ServerVersion</c>, <c>SessionId</c>) is not set here: the
/// <see cref="ILspTelemetryService"/> this service is given is the outermost identity-stamping
/// decorator (issue #844).
/// </para>
/// <para>
/// Privacy: only keys defined by <see cref="FeatureUsageCatalog"/> and their integer counts ever
/// reach the event — any other key is dropped at serialisation — so no <c>DocumentUri</c> and no
/// free-form detail can appear, matching <see cref="OperationDurationRecorder"/>'s own
/// <c>PerfSample</c> privacy posture.
/// </para>
/// </remarks>
public sealed class FeatureUsageFlushService : IFeatureUsageFlushService
{
    /// <summary>Name of the environment variable that overrides the flush interval, in seconds. A non-positive value disables periodic flushing.</summary>
    public const string FlushIntervalEnvVar = "REQNROLL_FEATURE_USAGE_FLUSH_INTERVAL_SECONDS";

    /// <summary>
    /// Whether the summary is sent when <see cref="FlushIntervalEnvVar"/> is unset. On: the event is
    /// aggregate counts from a closed catalogue, and the <c>REQNROLL_TELEMETRY_ENABLED</c> kill switch
    /// still applies in every IDE host (issue #851 argued against env-var-only opt-in gates, which
    /// collect no field data). Set the env var to <c>0</c> to disable.
    /// </summary>
    public const bool EnabledByDefault = true;

    /// <summary>Flush interval used when enabled and <see cref="FlushIntervalEnvVar"/> is unset.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly IFeatureUsageCounters _counters;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetry;
    private readonly TimeSpan? _interval;
    private readonly long _sessionStartTimestamp = Stopwatch.GetTimestamp();
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private long _sequence;

    /// <summary>Initializes a new instance of the <see cref="FeatureUsageFlushService"/> class.</summary>
    /// <param name="counters">The counters to drain.</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="telemetry">The (identity-stamping) telemetry sink; <c>null</c> drops events.</param>
    /// <param name="interval">Periodic flush interval; <c>null</c> disables the service (see <see cref="ResolveInterval"/>).</param>
    public FeatureUsageFlushService(
        IFeatureUsageCounters counters,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetry,
        TimeSpan? interval)
    {
        _counters = counters;
        _logger = logger;
        _telemetry = telemetry;
        _interval = interval;
    }

    /// <summary>
    /// Resolves the periodic flush interval from the raw <see cref="FlushIntervalEnvVar"/> value:
    /// a positive number of seconds enables it at that interval, a non-positive number disables it,
    /// and unset/unparseable falls back to <see cref="EnabledByDefault"/> / <see cref="DefaultInterval"/>.
    /// </summary>
    public static TimeSpan? ResolveInterval(string? raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        return EnabledByDefault ? DefaultInterval : null;
    }

    /// <summary>Resolves the interval from the process environment.</summary>
    public static TimeSpan? ResolveIntervalFromEnvironment() =>
        ResolveInterval(Environment.GetEnvironmentVariable(FlushIntervalEnvVar));

    /// <summary>
    /// Flushes <paramref name="service"/> (final) when the LSP <c>shutdown</c> request arrives on
    /// <paramref name="shutdown"/>. Hooked to the request rather than to process exit because by the
    /// time the server's exit completes the client has sent <c>exit</c> and the transport may
    /// already be closed, which would drop the notification.
    /// </summary>
    public static IDisposable FlushOnShutdown(IFeatureUsageFlushService service, IObservable<bool> shutdown) =>
        shutdown.Subscribe(new ShutdownObserver(service));

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
            // Normal shutdown path -- the shutdown request performs its own FlushFinalAsync.
        }
    }

    /// <inheritdoc/>
    public Task FlushFinalAsync()
    {
        // Disabled means "never send", including at shutdown.
        if (_interval is not null)
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

        var lookup = SerializeCounts(counts, FeatureUsageKind.Lookup);
        var passive = SerializeCounts(counts, FeatureUsageKind.Passive);
        var peak = SerializeCounts(counts, FeatureUsageKind.Peak);

        // Nothing allowlisted was drained (the usual idle case; or only unknown keys, which the
        // privacy guarantee must not depend on every IFeatureUsageCounters caller avoiding).
        if (lookup is null && passive is null && peak is null)
            return;

        var properties = new Dictionary<string, object?>
        {
            [TelemetryProperties.WindowSeconds] = Math.Round(windowSeconds),
            [TelemetryProperties.IsFinal] = isFinal,
            [TelemetryProperties.Sequence] = Interlocked.Increment(ref _sequence),
            [TelemetryProperties.SessionSeconds] = Math.Round(Stopwatch.GetElapsedTime(_sessionStartTimestamp).TotalSeconds),
        };
        if (lookup is not null) properties[TelemetryProperties.LookupCounts] = lookup;
        if (passive is not null) properties[TelemetryProperties.PassiveCounts] = passive;
        if (peak is not null) properties[TelemetryProperties.PeakCounts] = peak;

        _telemetry?.SendEvent(TelemetryEvents.FeatureUsageSummary, properties);
    }

    /// <summary>
    /// Serialises the entries of <paramref name="kind"/> as one compact, invariant, key-sorted JSON
    /// object string, or <c>null</c> if there are none. A plain string is the one shape that
    /// survives all three IDE forwarders unchanged (VS Code <c>String(value)</c>, Rider
    /// <c>toString()</c> and VS <c>JToken.ToString()</c> each mangle nested objects differently).
    /// Keys outside the catalogue are dropped.
    /// </summary>
    internal static string? SerializeCounts(IReadOnlyDictionary<string, long> counts, FeatureUsageKind kind)
    {
        var sorted = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in counts)
        {
            if (FeatureUsageCatalog.KindOf(key) == kind)
                sorted[key] = value;
        }

        return sorted.Count == 0 ? null : JsonSerializer.Serialize(sorted);
    }

    private sealed class ShutdownObserver : IObserver<bool>
    {
        private readonly IFeatureUsageFlushService _service;

        public ShutdownObserver(IFeatureUsageFlushService service) => _service = service;

        public void OnNext(bool value)
        {
            try
            {
                // FlushFinalAsync does its work synchronously and returns a completed task, so the
                // event has been handed to the transport before the shutdown response is produced;
                // a failing sink throws synchronously and is caught below.
                _ = _service.FlushFinalAsync();
            }
            catch (Exception)
            {
                // Best-effort telemetry must never fail the shutdown request.
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
