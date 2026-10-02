#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>Knobs for <see cref="TestOutcomesContentionScenario"/>.</summary>
/// <param name="CeilingRatio">
/// Matches <c>ResolveTestTargetsContentionOptions</c>'s 200x rather than
/// <c>WorkspaceReloadContentionScenario</c>'s 30x default: this storm is <see cref="ConcurrentCallers"/>
/// concurrent <em>requests</em>, each needing a real response, so it is the same shape (and the same
/// order of magnitude) as the #495 check. A regression ceiling, not a claimed steady-state ratio.
/// </param>
/// <param name="StreamSpacingMs">
/// How far apart the streamed results are. Must exceed the listener's 250ms throttle
/// (<c>TestOutcomeTcpListener.RefreshDebounce</c>) so that every result fires its own
/// <c>reqnroll/testOutcomes/changed</c> push — the fan-out being modelled. Results spaced further
/// apart than the throttle are exactly the case the listener's own remarks call out as unbounded.
/// </param>
public sealed record TestOutcomesContentionOptions(
    int Repetitions = 5,
    double CeilingRatio = 200.0,
    int SettleDelayMs = 500,
    int ConcurrentCallers = 200,
    int ScenarioCount = 2_000,
    int StreamedResults = 8,
    int StreamSpacingMs = 300);

/// <summary>
/// Dispatch-fairness scenario for the outcome pipeline's fan-out (issue #714, scenario C), following
/// <see cref="ResolveTestTargetsContentionScenario"/>'s shape: a synthetic run streams results
/// further apart than the 250ms throttle, so every one of them fires its own
/// <c>reqnroll/testOutcomes/changed</c> push, and each push makes every visible Run lens on the one
/// very large open <c>.feature</c> file re-resolve its target and re-pull its outcome — N pipelined
/// <c>resolveTestTargets</c> + <c>getOutcome</c> pairs, issued the way the clients issue them. That
/// storm races a cheap, unrelated <c>textDocument/foldingRange</c> read on a document it never
/// touches, and the P95 ratio against a same-run solo baseline is the check.
/// </summary>
/// <remarks>
/// This is the scenario most likely to expose a regression the rest of the suite cannot see: the
/// store's lookup takes its lock and then stats the container outside it (see
/// <c>GetTestOutcomeHandler.IsStale</c>), and the ingest path holds the same store lock while parsing
/// stdout — so the two behaviours the issue describes as "the real risk" meet here and nowhere else.
/// </remarks>
public sealed class TestOutcomesContentionScenario
{
    public const string Operation = "testOutcomes/changed#fanout-storm";

    private readonly BenchmarkLspHarness _harness;
    private readonly DocumentUri _largeFileUri;
    private readonly IReadOnlyList<Range> _scenarioRanges;
    private readonly OpenFeature _probe;
    private readonly SeededTestOutcome _seeded;
    private readonly TestOutcomesContentionOptions _options;

    private TestOutcomesContentionScenario(
        BenchmarkLspHarness harness, DocumentUri largeFileUri, IReadOnlyList<Range> scenarioRanges,
        OpenFeature probe, SeededTestOutcome seeded, TestOutcomesContentionOptions options)
    {
        _harness = harness;
        _largeFileUri = largeFileUri;
        _scenarioRanges = scenarioRanges;
        _probe = probe;
        _seeded = seeded;
        _options = options;
    }

    /// <summary>
    /// Opens the in-memory very-large feature (same synthetic shape #495 uses, via the shared
    /// builder) and returns the ready-to-run scenario. <paramref name="probe"/> must be a document the
    /// storm never touches, already open on <paramref name="harness"/>.
    /// </summary>
    public static async Task<TestOutcomesContentionScenario> CreateAsync(
        BenchmarkLspHarness harness, string corpusRoot, OpenFeature probe,
        SeededTestOutcome seeded, TestOutcomesContentionOptions options)
    {
        var (text, ranges) = ResolveTestTargetsContentionScenario.BuildLargeFeature(options.ScenarioCount);
        var path = Path.Combine(corpusRoot, "Features", "VeryLargeOutcomeFanOut.feature");
        var uri = DocumentUri.FromFileSystemPath(path);
        harness.OpenFeature(uri, 1, text);

        // Wait until the buffer has parsed (the same readiness signal the sibling scenarios poll)
        // before the storm starts.
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

        return new TestOutcomesContentionScenario(harness, uri, ranges, probe, seeded, options);
    }

    public async Task<ContentionCheck> RunAsync()
    {
        var baseline = new LatencyRecorder(Operation + "-baseline");
        var underLoad = new LatencyRecorder(Operation);

        for (var rep = 0; rep < _options.Repetitions; rep++)
        {
            // Solo baseline: the cheap read with no storm in flight.
            var baselineStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            baseline.Add(Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds);

            // The trigger: a live run whose results are spaced past the throttle, so each one pushes.
            var stream = StreamResultsAsync();

            // Give the first push time to land before the click-storm arrives.
            await Task.Delay(60).ConfigureAwait(false);

            var callers = new List<Task>(_options.ConcurrentCallers);
            for (var c = 0; c < _options.ConcurrentCallers; c++)
                callers.Add(ResolveTargetAndPullOutcomeAsync(_scenarioRanges[c % _scenarioRanges.Count]));
            var storm = Task.WhenAll(callers);

            // Measure only the probe's own round trip while the storm is in flight -- do not fold the
            // storm's settle time into this number.
            var probeStart = Stopwatch.GetTimestamp();
            await _harness.RequestFoldingRangeAsync(_probe.Uri).ConfigureAwait(false);
            underLoad.Add(Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds);

            await storm.ConfigureAwait(false);
            await stream.ConfigureAwait(false);

            if (_options.SettleDelayMs > 0)
                await Task.Delay(_options.SettleDelayMs).ConfigureAwait(false);
        }

        return new ContentionCheck(Operation, baseline.Summarize(), underLoad.Summarize(), _options.CeilingRatio);
    }

    /// <summary>One visible Run lens recomputing: resolve the target, then pull its outcome — in that order, on one connection.</summary>
    private async Task ResolveTargetAndPullOutcomeAsync(Range range)
    {
        await _harness.RequestResolveTestTargetsAsync(_largeFileUri, range).ConfigureAwait(false);
        await _harness.RequestGetTestOutcomeAsync(_seeded.AssemblyPath, _seeded.TypeFullName, _seeded.MethodName)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streams <see cref="TestOutcomesContentionOptions.StreamedResults"/> results over one loopback
    /// connection, spaced past the throttle. The rows carry the seeded container as their source and
    /// a short trace, so the ingest side does the store's real per-row work (parse, lock, upsert)
    /// while the storm is hammering the lookup and resolve paths.
    /// </summary>
    private async Task StreamResultsAsync()
    {
        var registration = await _harness.RequestRegisterTestRunAsync().ConfigureAwait(false);
        var endpoint = registration?.Endpoint;
        if (registration is null || !registration.Success || string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException("registerRun did not return an endpoint; cannot stream results.");

        var runId = registration.RunId ?? Guid.NewGuid().ToString("N");
        var rows = TestOutcomeScenarios.BuildBurstRows(_seeded.AssemblyPath, _options.StreamedResults, largeStdoutEveryNth: 0);

        using var connection = await TestOutcomeSeedConnection.ConnectAsync(endpoint!).ConfigureAwait(false);
        await connection.WriteHelloAsync(runId).ConfigureAwait(false);
        await connection.WriteRunStartAsync(runId, rows).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await connection.WriteResultAsync(runId, row).ConfigureAwait(false);
            await Task.Delay(_options.StreamSpacingMs).ConfigureAwait(false);
        }
        await connection.WriteRunCompleteAsync(runId, rows.Count).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
    }
}