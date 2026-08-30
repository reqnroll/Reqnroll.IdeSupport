namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Decides whether a given <c>PerfSample</c> telemetry event should be emitted. Field perf
/// instrumentation fires on every interactive request, so the telemetry metric is sampled to
/// bound event volume — the local log line (always written at verbose) is the unsampled record.
/// </summary>
public interface IPerformanceTelemetrySampler
{
    /// <summary>Returns whether the current perf sample should be emitted as telemetry (a random decision biased by the configured sample rate).</summary>
    bool ShouldSample();
}
