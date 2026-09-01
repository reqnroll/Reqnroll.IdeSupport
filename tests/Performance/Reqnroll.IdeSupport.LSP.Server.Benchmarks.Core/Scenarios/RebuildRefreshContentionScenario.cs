#nullable enable

using System.Diagnostics;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>Knobs for <see cref="RebuildRefreshContentionScenario"/>.</summary>
public sealed record RebuildRefreshContentionOptions(
    int Repetitions = 5,
    double CeilingRatio = 30.0,
    int SettleDelayMs = 300,
    int StormSize = 10);

/// <summary>
/// Dispatch-fairness / head-of-line-blocking scenario for issue #542: before that fix, a
/// <c>reqnroll/projectLoaded</c> re-send for an already-loaded project only re-triggered binding
/// discovery when the output path or target framework had changed — a plain rebuild does neither, so
/// Visual Studio's <c>OnBuildDone</c> re-send (its only rebuild signal, since it never registers the
/// output-assembly file watcher) was silently ignored. The fix makes every re-send always re-trigger
/// discovery, relying on <c>ConnectorDiscoveryService</c>'s assembly-hash guard to make a redundant
/// run (unchanged assembly) a cheap no-op downstream. This scenario is the regression guard for that
/// "cheap" half of the claim: fires a storm of repeated, identical <c>projectLoaded</c> notifications
/// (modelling several rapid rebuilds, or several projects in one solution finishing a build within the
/// same window) and races a cheap, unrelated <c>textDocument/foldingRange</c> read on a document the
/// storm never touches — same "storm vs. probe" shape as
/// <see cref="WorkspaceReloadContentionScenario"/> and <see cref="ResolveTestTargetsContentionScenario"/>.
/// If the hash-guard stops being cheap (or the discovery trigger stops being debounced/async), the
/// probe should get measurably slower under the storm.
/// </summary>
public sealed class RebuildRefreshContentionScenario
{
    public const string Operation = "reqnroll/projectLoaded#cheap-read-under-rebuild-refresh-storm";

    private readonly BenchmarkLspHarness _harness;
    private readonly string _corpusRoot;
    private readonly string _corpusAssemblyPath;
    private readonly OpenFeature _probe;
    private readonly RebuildRefreshContentionOptions _options;

    /// <param name="probe">
    /// A document the storm never touches — the "user is looking at something else while the build
    /// finishes" cheap-read target. Must already be open on <paramref name="harness"/>.
    /// </param>
    public RebuildRefreshContentionScenario(
        BenchmarkLspHarness harness, string corpusRoot, string corpusAssemblyPath, OpenFeature probe,
        RebuildRefreshContentionOptions options)
    {
        _harness = harness;
        _corpusRoot = corpusRoot;
        _corpusAssemblyPath = corpusAssemblyPath;
        _probe = probe;
        _options = options;
    }

    public async Task<ContentionCheck> RunAsync()
    {
        var baseline = new LatencyRecorder(Operation + "-baseline");
        var underLoad = new LatencyRecorder(Operation);

        for (var rep = 0; rep < _options.Repetitions; rep++)
        {
            // Solo baseline: the cheap read with no rebuild-refresh storm in flight.
            var baselineStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            baseline.Add(Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds);

            // The storm: identical projectLoaded notifications fired back to back, unawaited — the
            // same "issued together" shape a real client's build-finished event produces, not a
            // sequence of separately-awaited reloads. Every one now unconditionally re-triggers
            // discovery (issue #542) instead of being gated on the output path/TFM changing.
            for (var i = 0; i < _options.StormSize; i++)
                _harness.SendCorpusProjectLoaded(_corpusRoot, _corpusAssemblyPath);

            // Measure only the probe's own round-trip while the storm is in flight -- do not fold the
            // storm's own settle time into this number.
            var probeStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            underLoad.Add(Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds);

            // Let this repetition's storm (debounced re-discovery runs) settle before the next
            // repetition's baseline sample, so a straggler doesn't bleed into it.
            if (_options.SettleDelayMs > 0)
                await Task.Delay(_options.SettleDelayMs).ConfigureAwait(false);
        }

        return new ContentionCheck(Operation, baseline.Summarize(), underLoad.Summarize(), _options.CeilingRatio);
    }
}
