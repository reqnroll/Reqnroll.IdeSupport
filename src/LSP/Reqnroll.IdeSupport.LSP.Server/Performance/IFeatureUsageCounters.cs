namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// In-process, allocation-free counters for discrete-command feature usage (issue #582),
/// aggregated in memory and periodically flushed as one <c>FeatureUsageSummary</c> telemetry
/// event by <see cref="IFeatureUsageFlushService"/> instead of a <c>telemetry/event</c>
/// notification per invocation. Keeps the LSP request hot path free of serialization/wire work.
/// </summary>
public interface IFeatureUsageCounters
{
    /// <summary>
    /// Increments the counter for <paramref name="operation"/>. Hot path: must be
    /// allocation-free and lock-free (called from <see cref="IOperationDurationRecorder"/>'s
    /// existing handler-boundary sink, on whatever thread the operation completed on).
    /// </summary>
    void Increment(string operation);

    /// <summary>
    /// Atomically removes and returns all non-zero counts accrued since the last drain. A key
    /// incremented concurrently with this call is never lost — it either lands in this drain's
    /// result or survives intact for the next one.
    /// </summary>
    IReadOnlyDictionary<string, long> Drain();
}
