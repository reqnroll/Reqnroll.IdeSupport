namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Periodically drains <see cref="IFeatureUsageCounters"/> and emits the result as one
/// <c>FeatureUsageSummary</c> telemetry event, instead of a <c>telemetry/event</c> notification
/// per invocation (issue #582).
/// </summary>
public interface IFeatureUsageFlushService
{
    /// <summary>
    /// Runs the periodic flush loop until <paramref name="cancellationToken"/> is cancelled.
    /// A no-op (returns immediately) when the flush interval is not configured — see
    /// <see cref="FeatureUsageFlushService.FlushIntervalEnvVar"/> — so counting stays in-memory
    /// only and no telemetry is ever sent unless explicitly opted in.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drains and emits one event immediately, marked <c>IsFinal</c>. Intended for a best-effort
    /// call at graceful shutdown — see <see cref="FeatureUsageFlushService"/>'s remarks on the
    /// accepted loss profile for abnormal termination.
    /// </summary>
    Task FlushFinalAsync();
}
