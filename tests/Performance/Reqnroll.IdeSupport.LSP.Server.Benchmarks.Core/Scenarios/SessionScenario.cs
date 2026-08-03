#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>
/// Knobs for the editing-session scenario. Defaults favour a fast, CI-friendly run that still
/// exercises the cancellation path; raise <see cref="ThinkMs"/> / <see cref="TypingGapMs"/> to model
/// slower, more human pacing.
/// </summary>
public sealed record SessionOptions(
    int Warmup = 5,
    int Bursts = 40,
    double SupersedeRate = 0.30,
    int ThinkMs = 10,
    int TypingGapMs = 2,
    int NavigateEveryNthBurst = 5,
    int CodeLensEveryNthBurst = 10);

/// <summary>Aggregate request/cancellation outcomes for an editing session.</summary>
public sealed record SessionStats(
    double SupersedeRate,
    int ThinkMs,
    int TypingGapMs,
    int Bursts,
    long RequestsIssued,
    long RequestsCancelled,
    double CancellationRatePct,
    double MeanTimeToCancelMs);

/// <summary>The per-operation latencies plus the activity stats from one editing session.</summary>
public sealed record SessionResult(
    IReadOnlyList<(PerfTarget Target, LatencySummary Summary)> Results,
    SessionStats Stats);

/// <summary>
/// Models how an LSP is really driven: one user editing one active document at a time. Each edit
/// (<c>didChange</c>) triggers a <b>burst</b> of requests for that version — semantic tokens,
/// document outline, folding, completion — issued together (pipelined on the single connection) and
/// racing the server's <c>publishDiagnostics</c> push. A configurable fraction of bursts are
/// <b>superseded</b>: the next "keystroke" cancels the in-flight requests (<c>$/cancelRequest</c>),
/// exercising the cancellation path that real editors hammer. Think-time separates bursts so the
/// arrival pattern is realistic rather than a saturating hot loop.
/// <para>
/// Go-to-definition and CodeLens are <b>not</b> part of the per-edit burst — real editors don't fire
/// either on every keystroke (navigation is user-initiated; CodeLens is normally refreshed off a
/// debounced server push, not the edit itself), so both are instead pulled on their own slower,
/// fixed cadence (<see cref="SessionOptions.NavigateEveryNthBurst"/>/
/// <see cref="SessionOptions.CodeLensEveryNthBurst"/>) and, unlike the burst set, are never
/// superseded/cancelled — a real client doesn't cancel a navigation or a CodeLens refresh the way it
/// cancels stale semantic-tokens/completion requests mid-keystroke. The chosen cadence is a synthetic
/// stand-in for "occasional" (there is no single real-world rate to measure it against), useful for
/// realistic connection-sharing contention rather than as a claim about actual refresh frequency.
/// </para>
/// </summary>
/// <remarks>
/// This measures interactive latency <em>under contention</em> — a "reality check" that complements
/// the isolated per-operation scenarios (the "contract check" against the Performance Requirements).
/// Per-operation numbers here will be ≥ the isolated ones; that is the point.
/// </remarks>
public sealed class SessionScenario
{
    private readonly BenchmarkLspHarness _harness;
    private readonly IReadOnlyList<OpenFeature> _features;
    private readonly SessionOptions _options;

    // One recorder per operation. Within a burst each op type appears once and the four pulls write
    // to four *different* recorders, so concurrent Add() never targets the same recorder; bursts are
    // sequential, so cross-burst appends are serial too. No locking on the recorders is needed.
    private readonly LatencyRecorder _semanticTokens = new(PerfTargets.SemanticTokensFull.Operation);
    private readonly LatencyRecorder _completion = new(PerfTargets.CompletionStep.Operation);
    private readonly LatencyRecorder _documentSymbol = new(PerfTargets.DocumentSymbol.Operation);
    private readonly LatencyRecorder _foldingRange = new(PerfTargets.FoldingRange.Operation);
    private readonly LatencyRecorder _definition = new(PerfTargets.DefinitionCacheHit.Operation);
    private readonly LatencyRecorder _codeLens = new(PerfTargets.FeatureHookCodeLens.Operation);
    private readonly LatencyRecorder _diagnostics = new(PerfTargets.PublishDiagnostics.Operation);

    private long _issued;
    private long _cancelled;
    private double _totalCancelMs;
    private readonly object _cancelMsLock = new();

    public SessionScenario(BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> features, SessionOptions options)
    {
        _harness = harness;
        _features = features;
        _options = options;
    }

    public async Task<SessionResult> RunAsync()
    {
        var version = 2;
        for (var i = 0; i < _options.Warmup; i++)
            await EditBurstAsync(i, version++, record: false).ConfigureAwait(false);
        for (var i = 0; i < _options.Bursts; i++)
            await EditBurstAsync(i, version++, record: true).ConfigureAwait(false);

        var results = new List<(PerfTarget, LatencySummary)>();
        void AddIfSampled(PerfTarget target, LatencyRecorder rec)
        {
            if (rec.Count > 0) results.Add((target, rec.Summarize()));
        }
        AddIfSampled(PerfTargets.SemanticTokensFull, _semanticTokens);
        AddIfSampled(PerfTargets.CompletionStep, _completion);
        AddIfSampled(PerfTargets.DocumentSymbol, _documentSymbol);
        AddIfSampled(PerfTargets.FoldingRange, _foldingRange);
        AddIfSampled(PerfTargets.DefinitionCacheHit, _definition);
        AddIfSampled(PerfTargets.FeatureHookCodeLens, _codeLens);
        AddIfSampled(PerfTargets.PublishDiagnostics, _diagnostics);

        return new SessionResult(results, BuildStats());
    }

    private async Task EditBurstAsync(int i, int version, bool record)
    {
        var f = _features[i % _features.Count];
        var (line, character) = f.StepPosition;
        using var cts = new CancellationTokenSource();

        // 1. The keystroke. Start waiting for the diagnostics push immediately so we time from the edit.
        var editAt = Stopwatch.GetTimestamp();
        _harness.ChangeFeature(f.Uri, version, Mutate(f.Text, version));
        var diag = _harness.WaitForDiagnosticsAsync(f.Uri, editAt);

        // 2. The editor's reaction: issued together, awaited together (pipelined on one connection).
        var pulls = new[]
        {
            TimedAsync(_semanticTokens, record, ct => _harness.RequestSemanticTokensAsync(f.Uri, ct), cts.Token),
            TimedAsync(_completion, record, ct => _harness.RequestCompletionAsync(f.Uri, line, character, ct), cts.Token),
            TimedAsync(_documentSymbol, record, ct => _harness.RequestDocumentSymbolAsync(f.Uri, ct), cts.Token),
            TimedAsync(_foldingRange, record, ct => _harness.RequestFoldingRangeAsync(f.Uri, ct), cts.Token),
        };

        // 3. Fast typing: supersede a fraction of bursts — the next keystroke cancels the in-flight set.
        if (ShouldSupersede(i, _options.SupersedeRate))
        {
            if (_options.TypingGapMs > 0) await Task.Delay(_options.TypingGapMs).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(pulls).ConfigureAwait(false);

        // 4. Navigation (go-to-definition) fires on a slower cadence and is not superseded — users
        //    navigate far less often than they type, and rarely cancel a jump.
        if (_options.NavigateEveryNthBurst > 0 && i % _options.NavigateEveryNthBurst == 0)
            await TimedAsync(_definition, record,
                ct => _harness.RequestDefinitionAsync(f.Uri, line, character, ct), CancellationToken.None)
                .ConfigureAwait(false);

        // 4b. CodeLens fires on its own slower cadence too, and is likewise not superseded — see the
        //     class remarks for why it's excluded from the per-edit burst set. Requesting on the open
        //     .feature file exercises HookCodeLensHandler (StepCodeLensHandler only returns lenses
        //     for .cs files, which the session never opens).
        if (_options.CodeLensEveryNthBurst > 0 && i % _options.CodeLensEveryNthBurst == 0)
            await TimedAsync(_codeLens, record,
                ct => _harness.RequestCodeLensAsync(f.Uri, ct), CancellationToken.None)
                .ConfigureAwait(false);

        // 5. Record the diagnostics push latency (it raced the pulls).
        var diagMs = await diag.ConfigureAwait(false);
        if (record && diagMs is not null) _diagnostics.Add(diagMs.Value);

        // 6. Think-time before the next burst — a realistic arrival pattern, not a hot loop.
        if (_options.ThinkMs > 0) await Task.Delay(_options.ThinkMs).ConfigureAwait(false);
    }

    /// <summary>
    /// Times <paramref name="op"/>, recording its latency on success or counting it as a superseded
    /// (cancelled) request. Cancellation is swallowed so the burst's <see cref="Task.WhenAll(Task[])"/>
    /// never faults.
    /// </summary>
    private async Task TimedAsync(LatencyRecorder rec, bool record, Func<CancellationToken, Task> op, CancellationToken ct)
    {
        if (record) Interlocked.Increment(ref _issued);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await op(ct).ConfigureAwait(false);
            if (record) rec.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            if (!record) return;
            Interlocked.Increment(ref _cancelled);
            lock (_cancelMsLock) _totalCancelMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Whether burst <paramref name="burstIndex"/> is superseded. Evenly distributes ~<paramref
    /// name="rate"/> of bursts (Bresenham-style), so the schedule is deterministic and reproducible
    /// rather than random.
    /// </summary>
    public static bool ShouldSupersede(int burstIndex, double rate) =>
        (int)((burstIndex + 1) * rate) != (int)(burstIndex * rate);

    private static string Mutate(string text, int version) => text + $"\n  # session edit {version}\n";

    private SessionStats BuildStats()
    {
        var issued = Interlocked.Read(ref _issued);
        var cancelled = Interlocked.Read(ref _cancelled);
        double mean;
        lock (_cancelMsLock) mean = cancelled > 0 ? _totalCancelMs / cancelled : 0.0;
        var ratePct = issued > 0 ? 100.0 * cancelled / issued : 0.0;
        return new SessionStats(
            _options.SupersedeRate, _options.ThinkMs, _options.TypingGapMs, _options.Bursts,
            issued, cancelled, ratePct, mean);
    }
}
