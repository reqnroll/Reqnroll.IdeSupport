#nullable enable

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

/// <summary>
/// A dispatch-fairness / head-of-line-blocking check (issue #488): does a cheap, unrelated request
/// stay cheap while a workspace-wide storm of concurrent activity (e.g. many editor tabs restoring
/// at once) is in flight? Unlike <see cref="PerfTarget"/>/<see cref="OperationResult"/>, which
/// assert an absolute millisecond ceiling, this asserts a <b>ratio</b> of the under-load P95 to a
/// same-run solo baseline P95.
/// </summary>
/// <remarks>
/// A ratio, not an absolute-ms target, is deliberate: the #471/#477 investigation
/// (<c>ConcurrencyProbeTests</c>'s own history) found the measured stall's absolute magnitude swings
/// wildly with machine speed and concurrent CPU load (~15x-20x locally in isolation, ~1.3x-40x+
/// under CI/parallel-test contention) — an absolute-ms ceiling on either the baseline or the
/// under-load number would either never fire or fire constantly depending on the machine. The ratio
/// to a baseline measured in the very same run cancels most of that variance out, leaving a
/// generous <see cref="CeilingRatio"/> as a regression ceiling for "did dispatch fairness get
/// dramatically worse," not a claim about a specific steady-state ratio.
/// </remarks>
public sealed record ContentionCheck(
    string Operation,
    LatencySummary Baseline,
    LatencySummary UnderLoad,
    double CeilingRatio)
{
    /// <summary>How much slower the cheap request got under the storm, at P95.</summary>
    public double RatioAtP95 => UnderLoad.P95Ms / Baseline.P95Ms;

    /// <summary>True when the P95 ratio stays within the regression ceiling.</summary>
    public bool MeetsTarget => RatioAtP95 <= CeilingRatio;
}
