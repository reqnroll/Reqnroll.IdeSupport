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
/// Investigation probe for issue #471 ("LSP server: workspace/inlayHint/refresh and
/// workspace/semanticTokens/refresh scale badly on large solutions, blocking unrelated requests
/// for tens of seconds"). Answers the fork-in-the-road question the issue leaves open — "Confirm
/// whether the server's request dispatch is intentionally serial" — empirically, against the real
/// in-process server, rather than by reading OmniSharp internals.
/// </summary>
/// <remarks>
/// Method: fire a batch of concurrent <c>textDocument/codeLens</c> requests against the corpus's
/// binding file (64 patterns; each lens computation scans <see cref="BindingMatchService"/>'s
/// full match-set cache via <c>FindUsages</c> — see <c>StepCodeLensHandler</c> — which is the #1
/// suspect for the workload that's actually saturating the server while the corpus's 1,350 cached
/// steps sit in memory, close to the issue's reported ~1,300-method scale), then fire one cheap,
/// unrelated <c>textDocument/foldingRange</c> request on a different document at the same time.
/// </remarks>
/// <remarks>
/// <b>Regression gate, not a pure diagnostic (tightened once the root cause was confirmed — see
/// issue #471):</b> decompiling OmniSharp 0.19.9 confirmed <c>textDocument/codeLens</c> and
/// <c>textDocument/foldingRange</c> are both <c>[Parallel]</c>-tagged by the library, so they
/// should genuinely run concurrently — the slowdown this test asserts below is not "inherent to
/// LSP," it's a symptom of the dispatch pipeline (a slow <c>[Serial]</c>-tagged
/// <c>textDocument/didOpen</c>/<c>didChange</c> stalls the whole pipeline until it drains — see
/// the issue for the full mechanism).
/// </remarks>
/// <remarks>
/// <b>History (see #477):</b> real runs originally measured 45x-56x, asserted as a &gt;5x floor
/// documenting known-bad behavior. #477 fixed one confirmed contributor — four
/// <c>_ = _mediator.Publish(...)</c> discards that looked fire-and-forget but actually ran their
/// entire reparse cascade inline on the calling <c>[Serial]</c> handler's thread — and measurably
/// improved this: local runs on a dev machine post-fix land around 16x-19x (down from 45x-56x),
/// and one CI run measured as low as ~1.3x. That CI number is <i>not</i> the reliable new
/// steady-state, though — it doesn't reproduce locally across five separate runs — so this is not
/// yet a "the stall is gone, flip the assertion to require good behavior" situation the class's
/// history once anticipated it would become. The residual ~16x-19x is plausibly genuine CPU
/// contention from 20 concurrent <c>FindUsages</c> scans saturating the machine rather than a
/// dispatch-blocking bug, but that's unconfirmed — worth its own investigation under #471, not
/// something to declare fixed here. Until then this is a <b>regression ceiling</b>: normal
/// (reduced but nonzero, and evidently environment-variable) stall passes; a return toward the
/// original, fully-unfixed 45x-56x magnitude fails loudly.
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

        // Regression ceiling, not a "must reproduce the bug" floor (see the class remarks and
        // #477): the original >5x floor asserted known-bad, fully-unfixed behavior. #477 reduced
        // but did not eliminate this stall, and the residual magnitude has proven noisy across
        // environments (~16x-19x on a dev machine, ~1.3x on one CI run) rather than converging to
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
