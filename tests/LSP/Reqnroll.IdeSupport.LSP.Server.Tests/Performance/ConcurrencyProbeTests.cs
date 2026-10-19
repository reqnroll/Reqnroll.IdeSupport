#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Concurrency probe: does a slow <c>textDocument/codeLens</c> workload block unrelated requests?
/// Large solutions saw <c>workspace/inlayHint/refresh</c> and <c>workspace/semanticTokens/refresh</c>
/// scale badly and block other requests for tens of seconds. This test answers whether the server's
/// request dispatch is intentionally serial, empirically against the real in-process server rather
/// than by reading OmniSharp internals.
/// </summary>
/// <remarks>
/// Method: fire a batch of concurrent <c>textDocument/codeLens</c> requests against the corpus's
/// binding file (64 patterns; each lens computation scans <see cref="BindingMatchService"/>'s
/// full match-set cache via <c>FindUsages</c> — see <c>StepCodeLensHandler</c> — the likeliest
/// source of load while the corpus's 1,350 cached steps sit in memory, close to the ~1,300-method
/// scale seen on large solutions), then fire one cheap, unrelated <c>textDocument/foldingRange</c>
/// request on a different document at the same time.
/// </remarks>
/// <remarks>
/// <b>Regression gate, not a pure diagnostic:</b> OmniSharp 0.19.9 tags <c>textDocument/codeLens</c>
/// and <c>textDocument/foldingRange</c> as <c>[Parallel]</c>, so they should genuinely run
/// concurrently — the slowdown this test asserts below is not inherent to LSP, it is a symptom of
/// the dispatch pipeline (a slow <c>[Serial]</c>-tagged <c>textDocument/didOpen</c>/<c>didChange</c>
/// stalls the whole pipeline until it drains).
/// </remarks>
/// <remarks>
/// <b>Calibration:</b> with <c>_ = _mediator.Publish(...)</c> discards that look fire-and-forget but
/// actually run their entire reparse cascade inline on the calling <c>[Serial]</c> handler's thread,
/// the stall measured 45x-56x the solo baseline. With those discards made genuinely fire-and-forget,
/// local runs on a dev machine land around 16x-19x, and one CI run measured as low as ~1.3x. That CI
/// number is <i>not</i> a reliable steady state — it does not reproduce locally — so the assertion
/// cannot require good behavior outright. The residual ~16x-19x is plausibly genuine CPU contention
/// from 20 concurrent <c>FindUsages</c> scans saturating the machine rather than a dispatch-blocking
/// bug, but that is unconfirmed. So this is a <b>regression ceiling</b>: normal (reduced but
/// nonzero, and environment-dependent) stall passes; a return toward the 45x-56x magnitude fails
/// loudly.
/// </remarks>
public class ConcurrencyProbeTests
{
    [Fact]
    public async Task Cheap_request_latency_under_concurrent_codeLens_load()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);

        // Populate BindingMatchService's cache to the corpus's full 1,350 steps (matches the
        // issue's reported scale) and register the binding file so StepCodeLensHandler has a
        // real registry + real usages to scan for every lens.
        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, count: 50);
        features.Should().NotBeEmpty();

        // Registers the corpus as a loaded project (same call BenchmarkRunner makes before its own
        // StepCodeLensAsync scenario) so StepCodeLensHandler has a real registry to look up — plain
        // OpenCSharp without this never resolves an owning project and CodeLens stays empty. The
        // assembly path need not exist: Roslyn source-level discovery (driven by OpenCSharp below)
        // populates the registry directly from source, not from the compiled DLL.
        harness.SendCorpusProjectLoaded(corpusRoot, Path.Combine(corpusRoot, "does-not-exist.dll"));

        var csPath = Path.Combine(corpusRoot, "Bindings", "CorpusSteps.cs");
        var csUri = DocumentUri.FromFileSystemPath(csPath);
        harness.OpenCSharp(csUri, 1, File.ReadAllText(csPath));

        // Let Roslyn discovery + the initial parse/match settle before measuring anything.
        var warmupLenses = await WaitForNonEmptyCodeLensAsync(harness, csUri);
        warmupLenses.Should().BeTrue("StepCodeLensHandler must return real lenses for this probe to exercise FindUsages");

        var probeUri = features[0].Uri;

        // Solo baseline: the cheap request with no concurrent load.
        var baselineStart = Stopwatch.GetTimestamp();
        await harness.RequestFoldingRangeAsync(probeUri);
        var baselineMs = Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds;

        // Sanity: a solo folding-range on a tiny file should never itself be slow. If this fails,
        // the environment/harness is the problem, not the dispatch behavior under test below.
        baselineMs.Should().BeLessThan(500,
            "a solo folding-range request has no concurrent load and should be fast regardless of environment noise");

        // Concurrent: fire a batch of CodeLens requests (the suspected expensive, unindexed
        // FindUsages scan) and the same cheap request at the same time, without awaiting the
        // CodeLens calls first.
        const int concurrentCodeLensCount = 20;
        var codeLensTasks = new Task[concurrentCodeLensCount];
        for (var i = 0; i < concurrentCodeLensCount; i++)
            codeLensTasks[i] = harness.RequestCodeLensAsync(csUri);

        // Measure only the probe's own round-trip while the CodeLens batch is in flight — do not
        // fold the CodeLens batch's own completion time into this number.
        var probeStart = Stopwatch.GetTimestamp();
        await harness.RequestFoldingRangeAsync(probeUri);
        var underLoadMs = Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds;

        // Drain the CodeLens batch afterward so its tasks are observed (avoid unobserved-exception
        // noise); this does not affect the measurement above.
        await Task.WhenAll(codeLensTasks);

        Console.WriteLine("=== Issue #471 concurrency probe ===");
        Console.WriteLine($"Corpus: {features.Count} feature files opened, ~1350 cached steps, 64 binding patterns.");
        Console.WriteLine($"Solo folding-range baseline:                {baselineMs:F1} ms");
        Console.WriteLine($"Folding-range latency under {concurrentCodeLensCount} concurrent codeLens calls: {underLoadMs:F1} ms " +
                           $"({underLoadMs / baselineMs:F1}x baseline)");

        // Regression ceiling, not a "must reproduce the bug" floor (see the class remarks): a floor
        // would assert known-bad behavior. The stall is reduced but not eliminated, and the
        // residual magnitude is noisy across environments (~16x-19x on a dev machine, ~1.3x on one CI run) rather than converging to
        // a small, stable ratio -- so a tight floor or ceiling around that residual would just
        // move the flakiness, not remove it. 30x sits well below the original 45x-56x while
        // comfortably clearing the observed post-fix range on both sides, so it still catches a
        // real regression back toward the fully-unfixed magnitude.
        underLoadMs.Should().BeLessThan(baselineMs * 30,
            "if this reproduces anywhere near the original, fully-unfixed 45x-56x measured before " +
            "#477's fire-and-forget fix, that fix's improvement has likely regressed");
    }

    private static async Task<bool> WaitForNonEmptyCodeLensAsync(
        BenchmarkLspHarness harness, DocumentUri uri, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var lenses = await harness.RequestCodeLensAsync(uri).ConfigureAwait(false);
            if (lenses is { Length: > 0 }) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }
}
