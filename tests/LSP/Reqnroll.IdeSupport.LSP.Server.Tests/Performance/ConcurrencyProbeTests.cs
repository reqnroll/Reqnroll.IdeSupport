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
/// If request dispatch is serial/single-threaded, the cheap request's latency should climb with
/// the size of the concurrent CodeLens batch (it queues behind them); if dispatch is concurrent,
/// its latency should stay close to its own solo baseline regardless of batch size. This is a
/// diagnostic, not a regression gate — it logs its findings to the test output rather than
/// asserting a specific outcome, since the answer is exactly what's under investigation.
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
        Console.WriteLine($"Folding-range latency under {concurrentCodeLensCount} concurrent codeLens calls: {underLoadMs:F1} ms");
        Console.WriteLine(underLoadMs > baselineMs * 3
            ? "=> Cheap request latency scales with concurrent CodeLens load: consistent with SERIAL request dispatch."
            : "=> Cheap request latency stayed near baseline despite concurrent CodeLens load: consistent with CONCURRENT request dispatch.");
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
