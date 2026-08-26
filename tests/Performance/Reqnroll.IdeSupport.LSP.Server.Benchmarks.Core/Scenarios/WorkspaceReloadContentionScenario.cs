#nullable enable

using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>Knobs for <see cref="WorkspaceReloadContentionScenario"/>.</summary>
public sealed record WorkspaceReloadContentionOptions(
    int Repetitions = 5,
    double CeilingRatio = 30.0,
    int SettleDelayMs = 300);

/// <summary>
/// Dispatch-fairness / head-of-line-blocking scenario (issue #488, following up on #471/#477):
/// models an editor restoring many already-open tabs at once, or a workspace-wide event (a branch
/// switch, a <c>reqnroll.json</c> change, a solution reload) that fans a reaction out across every
/// open feature file simultaneously — the exact "reparse-every-open-feature-file cascade" shape
/// #477 fixed one contributor to — racing a cheap, unrelated <c>textDocument/foldingRange</c> read
/// on a document the storm never touches.
/// </summary>
/// <remarks>
/// Unlike <see cref="SessionScenario"/> (one simulated user, one active document, modest per-burst
/// concurrency) this deliberately fires a large, workspace-wide burst of <c>didChange</c>
/// notifications — issued together, not awaited individually, the same "pipelined on one
/// connection" shape a real client uses — to reach the scale/shape that actually saturated the
/// dispatch pipeline in the field, which the isolated per-operation scenarios and the one-user
/// session scenario both undershoot.
/// </remarks>
public sealed class WorkspaceReloadContentionScenario
{
    public const string Operation = "workspace/tabs-restored#cheap-read-under-reload-storm";

    private readonly BenchmarkLspHarness _harness;
    private readonly IReadOnlyList<OpenFeature> _restoredFiles;
    private readonly OpenFeature _probe;
    private readonly WorkspaceReloadContentionOptions _options;

    /// <param name="restoredFiles">
    /// The "many tabs" that react together on each repetition's storm. Must already be open on
    /// <paramref name="harness"/> (mirrors a real editor: the tabs were restored/opened before the
    /// reload event fires).
    /// </param>
    /// <param name="probe">
    /// A document the storm never edits — the "user is looking at something else" cheap-read target.
    /// Must also already be open, and must not appear in <paramref name="restoredFiles"/>.
    /// </param>
    public WorkspaceReloadContentionScenario(
        BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> restoredFiles, OpenFeature probe,
        WorkspaceReloadContentionOptions options)
    {
        _harness = harness;
        _restoredFiles = restoredFiles;
        _probe = probe;
        _options = options;
    }

    public async Task<ContentionCheck> RunAsync()
    {
        var baseline = new LatencyRecorder(Operation + "-baseline");
        var underLoad = new LatencyRecorder(Operation);
        var version = 1000;

        for (var rep = 0; rep < _options.Repetitions; rep++)
        {
            // Solo baseline: the cheap read with no concurrent storm in flight.
            var baselineStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            baseline.Add(Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds);

            // The reload storm: every restored tab reacts at once. Notifications are fired back to
            // back without awaiting a response (didChange has none) -- the same "issued together"
            // shape a real client's file watcher/reload event produces on one connection, not a
            // sequence of separately-awaited edits.
            version++;
            foreach (var f in _restoredFiles)
                _harness.ChangeFeature(f.Uri, version, f.Text + $"\n  # reload-storm rep {rep} v{version}\n");

            // Measure only the probe's own round-trip while the storm is in flight -- do not fold
            // the storm's own settle time into this number.
            var probeStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            underLoad.Add(Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds);

            // Let this repetition's storm (reparse/diagnostics/debounced refreshes) settle before
            // the next repetition's baseline sample, so a straggler doesn't bleed into it.
            if (_options.SettleDelayMs > 0)
                await Task.Delay(_options.SettleDelayMs).ConfigureAwait(false);
        }

        return new ContentionCheck(Operation, baseline.Summarize(), underLoad.Summarize(), _options.CeilingRatio);
    }
}
