#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>Knobs for <see cref="ResolveTestTargetsContentionScenario"/>.</summary>
/// <param name="CeilingRatio">
/// Higher than <c>WorkspaceReloadContentionScenario</c>'s 30x default (issue #488) — that scenario's
/// storm is <see cref="ConcurrentCallers"/>-many fire-and-forget <c>didChange</c> notifications
/// (no response to wait for), while this one's storm is that many concurrent <em>requests</em>, each
/// needing a real response round-tripped back through the same dispatch pipeline the probe read
/// shares. Measured locally (200 concurrent callers) at ~140x; 200x leaves headroom above that
/// baseline as a placeholder pending real reference-machine calibration data, consistent with this
/// check's own "regression ceiling, not a claimed steady-state ratio" purpose (see
/// <see cref="Latency.ContentionCheck"/>'s remarks).
/// </param>
public sealed record ResolveTestTargetsContentionOptions(
    int Repetitions = 5,
    double CeilingRatio = 200.0,
    int SettleDelayMs = 300,
    int ConcurrentCallers = 200,
    int ScenarioCount = 2_000);

/// <summary>
/// Dispatch-fairness scenario for the Run CodeLens bridge (issue #495): models N visible Run
/// CodeLens/CodeVision entries all resolving at once against one very large <c>.feature</c> file —
/// the exact "VeryLargeFeature" stress-corpus shape the issue's live report used (~2,000-2,400
/// scenarios in one file) — racing a cheap, unrelated <c>textDocument/foldingRange</c> read on a
/// document the storm never touches. Follows <see cref="WorkspaceReloadContentionScenario"/>'s shape
/// (issue #488) as instructed by issue #495's own scope note, rather than reinventing the
/// baseline/under-load/ceiling-ratio pattern.
/// </summary>
/// <remarks>
/// Unlike the isolated <c>InteractiveScenarios.ResolveTestTargetsAsync</c> (one caller, one range,
/// per-call latency), this fires <see cref="ResolveTestTargetsContentionOptions.ConcurrentCallers"/>
/// requests together — issued back to back without awaiting each one individually, the same
/// "pipelined on one connection" shape N simultaneously-visible Run lenses (VS's per-line data
/// points, VS Code's lazily-resolved lenses, Rider's <c>CodeVisionProvider</c> recompute) produce
/// against a single huge file — against a rotating set of distinct scenario ranges in the one large
/// file, so the storm exercises the same dispatch path #495's per-target fix targets rather than
/// N requests for the identical range.
/// </remarks>
public sealed class ResolveTestTargetsContentionScenario
{
    public const string Operation = "reqnroll/resolveTestTargets#concurrent-codelens-callers";

    private readonly BenchmarkLspHarness _harness;
    private readonly DocumentUri _largeFileUri;
    private readonly IReadOnlyList<Range> _scenarioRanges;
    private readonly OpenFeature _probe;
    private readonly ResolveTestTargetsContentionOptions _options;

    private ResolveTestTargetsContentionScenario(
        BenchmarkLspHarness harness, DocumentUri largeFileUri, IReadOnlyList<Range> scenarioRanges,
        OpenFeature probe, ResolveTestTargetsContentionOptions options)
    {
        _harness = harness;
        _largeFileUri = largeFileUri;
        _scenarioRanges = scenarioRanges;
        _probe = probe;
        _options = options;
    }

    /// <summary>
    /// Builds the very-large synthetic feature file in memory (not part of the pinned corpus — this
    /// shape exists only for this scenario), opens it on <paramref name="harness"/>, waits for it to
    /// parse, and returns the ready-to-run scenario.
    /// </summary>
    /// <param name="corpusRoot">
    /// The committed corpus root — the synthetic large file's URI is rooted under its
    /// <c>Features/</c> folder (via <see cref="DocumentUri.FromFileSystemPath"/>, same as every real
    /// corpus feature) purely so it round-trips through <see cref="Uri"/> parsing correctly
    /// server-side; the file itself is never written to disk, only opened via <c>didOpen</c>.
    /// </param>
    /// <param name="probe">A document already open on <paramref name="harness"/> that the storm never
    /// touches — the "user is looking at something else" cheap-read target. Must not be the large
    /// file this scenario generates.</param>
    public static async Task<ResolveTestTargetsContentionScenario> CreateAsync(
        BenchmarkLspHarness harness, string corpusRoot, OpenFeature probe, ResolveTestTargetsContentionOptions options)
    {
        var (text, ranges) = BuildLargeFeature(options.ScenarioCount);
        var path = System.IO.Path.Combine(corpusRoot, "Features", "VeryLargeFeature.feature");
        var uri = DocumentUri.FromFileSystemPath(path);
        harness.OpenFeature(uri, 1, text);

        // Wait until the buffer has actually parsed (same "poll semantic tokens" readiness signal
        // InteractiveScenarios.OpenFeaturesAsync uses) before the storm starts, so the first
        // repetition doesn't race the initial parse itself.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var tokens = await harness.RequestAsync<SemanticTokens?>(
                "textDocument/semanticTokens/full",
                new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
                .ConfigureAwait(false);
            if (tokens is { Data.Length: > 0 }) break;
            await Task.Delay(50).ConfigureAwait(false);
        }

        return new ResolveTestTargetsContentionScenario(harness, uri, ranges, probe, options);
    }

    public async Task<ContentionCheck> RunAsync()
    {
        var baseline = new LatencyRecorder(Operation + "-baseline");
        var underLoad = new LatencyRecorder(Operation);

        for (var rep = 0; rep < _options.Repetitions; rep++)
        {
            // Solo baseline: the cheap read with no concurrent storm in flight.
            var baselineStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            baseline.Add(Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds);

            // The CodeLens-driven storm: many concurrent resolveTestTargets calls against distinct
            // scenario ranges in the one large file, fired together rather than awaited one at a
            // time — mirrors N visible Run lenses all resolving at once on file open/scroll.
            var callers = new List<Task>(_options.ConcurrentCallers);
            for (var c = 0; c < _options.ConcurrentCallers; c++)
            {
                var range = _scenarioRanges[c % _scenarioRanges.Count];
                callers.Add(_harness.RequestResolveTestTargetsAsync(_largeFileUri, range));
            }
            var storm = Task.WhenAll(callers);

            // Measure only the probe's own round-trip while the storm is in flight -- do not fold
            // the storm's own settle time into this number.
            var probeStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            underLoad.Add(Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds);

            await storm.ConfigureAwait(false);

            // Let this repetition's storm fully settle before the next repetition's baseline
            // sample, so a straggler doesn't bleed into it.
            if (_options.SettleDelayMs > 0)
                await Task.Delay(_options.SettleDelayMs).ConfigureAwait(false);
        }

        return new ContentionCheck(Operation, baseline.Summarize(), underLoad.Summarize(), _options.CeilingRatio);
    }

    /// <summary>
    /// Builds a single synthetic <c>.feature</c> file with <paramref name="scenarioCount"/> plain
    /// scenarios, each a fixed 3 lines (header + one bound step + blank line), and returns each
    /// scenario header's own zero-width range alongside the text. No <c>Scenario Outline</c>/tags/
    /// hooks — this scenario measures dispatch fairness under many concurrent resolution requests,
    /// not resolution correctness (which the isolated <c>InteractiveScenarios.ResolveTestTargetsAsync</c>
    /// and the server-side <c>ScenarioTestTargetResolverTests</c> already cover).
    /// </summary>
    private static (string Text, IReadOnlyList<Range> ScenarioRanges) BuildLargeFeature(int scenarioCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Feature: Very large feature (resolveTestTargets contention, issue #495)");
        sb.AppendLine();

        var ranges = new List<Range>(scenarioCount);
        const int firstScenarioLine = 2;
        for (var s = 0; s < scenarioCount; s++)
        {
            var headerLine = firstScenarioLine + s * 3;
            sb.AppendLine($"  Scenario: Scenario {s}");
            sb.AppendLine($"    Given precondition {s} is met");
            sb.AppendLine();
            ranges.Add(new Range(new Position(headerLine, 2), new Position(headerLine, 2)));
        }

        return (sb.ToString(), ranges);
    }
}
