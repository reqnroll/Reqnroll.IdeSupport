namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// In-process, allocation-free counters for high-volume lookup and passive feature usage (issue #582),
/// aggregated in memory and periodically flushed as one <c>FeatureUsageSummary</c> telemetry
/// event by <see cref="IFeatureUsageFlushService"/> instead of a <c>telemetry/event</c>
/// notification per invocation. Keeps the LSP request hot path free of serialization/wire work.
/// </summary>
public interface IFeatureUsageCounters
{
    /// <summary>
    /// Increments the counter for <paramref name="key"/> (a <see cref="FeatureUsageEntry.Key"/>). Hot path: must be
    /// allocation-free and lock-free (called from <see cref="IOperationDurationRecorder"/>'s
    /// existing handler-boundary sink, on whatever thread the operation completed on).
    /// </summary>
    void Increment(string key);

    /// <summary>
    /// Records <paramref name="value"/> for gauge <paramref name="key"/>, keeping only the highest value
    /// seen since the last <see cref="Drain"/> (a high-water mark; no per-document or per-file data is
    /// retained). Non-positive values are ignored, so an idle gauge stays out of the drain. Same
    /// hot-path constraints as <see cref="Increment"/>.
    /// </summary>
    void Observe(string key, long value);

    /// <summary>
    /// Atomically removes and returns all non-zero counts and gauge peaks accrued since the last drain (counts and peaks share the one dictionary; the catalogue's <see cref="FeatureUsageKind"/> tells them apart). A key
    /// incremented concurrently with this call is never lost — it either lands in this drain's
    /// result or survives intact for the next one.
    /// </summary>
    IReadOnlyDictionary<string, long> Drain();
}
