#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Investigation probe for issue #471 (item #3 of the investigation plan): a fast, CI-runnable
/// duration-vs-cache-size curve for <c>textDocument/codeLens</c>, to confirm/quantify the shape of
/// <c>BindingMatchService.FindUsages</c>'s cost (called once per binding by
/// <see cref="Reqnroll.IdeSupport.LSP.Server.Features.CodeLens.StepCodeLensHandler"/>, an unindexed
/// scan over the whole match-set cache) as the cache grows — without needing the full VS +
/// <c>Reqnroll.VeryLargeFeature</c> manual repro.
/// </summary>
/// <remarks>
/// Method: open the corpus's binding file (64 patterns) once, then incrementally open more of the
/// corpus's 50 feature files (1,350 steps total), measuring <c>textDocument/codeLens</c> latency
/// at each step count. If cost tracks cached step count roughly linearly (bindings-in-file is
/// fixed at 64; only cached-step count grows), per-step cost (ms / cacheSteps) should stay
/// roughly flat across the run; if it instead climbs, that points to a worse-than-linear
/// (e.g. re-scanning already-scanned state, or growth in something other than cache size) shape.
/// Diagnostic, not a regression gate — logs its findings rather than asserting a specific curve
/// shape, since the shape is exactly what's under investigation.
/// </remarks>
public class FindUsagesScalingProbeTests
{
    [Fact]
    public async Task CodeLens_latency_vs_cached_step_count()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);
        harness.SendCorpusProjectLoaded(corpusRoot, Path.Combine(corpusRoot, "does-not-exist.dll"));

        var csPath = Path.Combine(corpusRoot, "Bindings", "CorpusSteps.cs");
        var csUri = DocumentUri.FromFileSystemPath(csPath);
        harness.OpenCSharp(csUri, 1, File.ReadAllText(csPath));

        var allFeaturePaths = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // Warm up: wait until the registry is populated and CodeLens returns real lenses before
        // measuring anything (Roslyn discovery + first parse/match settling).
        (await WaitForNonEmptyCodeLensAsync(harness, csUri)).Should()
            .BeTrue("StepCodeLensHandler must return real lenses for this probe to exercise FindUsages");

        int[] featureCountTargets = [2, 10, 20, 30, 40, 50];
        var opened = 0;
        var version = 1;
        var results = new List<(int Features, double MedianMs)>();

        foreach (var target in featureCountTargets)
        {
            while (opened < target && opened < allFeaturePaths.Length)
            {
                var path = allFeaturePaths[opened];
                harness.OpenFeature(DocumentUri.FromFileSystemPath(path), version++, File.ReadAllText(path));
                opened++;
            }

            // Let the newly opened files' parse/match settle before measuring.
            await SettleAsync(harness, allFeaturePaths[opened - 1]);

            const int samplesPerPoint = 5;
            var samples = new List<double>(samplesPerPoint);
            for (var i = 0; i < samplesPerPoint; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await harness.RequestCodeLensAsync(csUri);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            results.Add((opened, samples[samples.Count / 2]));
        }

        Console.WriteLine("=== Issue #471 FindUsages scaling probe (textDocument/codeLens on 64-pattern binding file) ===");
        Console.WriteLine("features  medianMs  msPerFeature");
        foreach (var (features, medianMs) in results)
            Console.WriteLine($"{features,8}  {medianMs,8:F2}  {(medianMs / features),12:F3}");

        // Compare the per-feature (proxy for per-cached-step) cost at the smallest and largest
        // measured points. A flat ratio is consistent with linear growth in cache size; a ratio
        // that climbs well beyond measurement noise points to worse-than-linear growth.
        var first = results.First();
        var last = results.Last();
        var firstPerFeature = first.MedianMs / first.Features;
        var lastPerFeature = last.MedianMs / last.Features;
        Console.WriteLine($"Per-feature cost at {first.Features} features: {firstPerFeature:F3} ms/feature");
        Console.WriteLine($"Per-feature cost at {last.Features} features:  {lastPerFeature:F3} ms/feature");
        Console.WriteLine(lastPerFeature > firstPerFeature * 2
            ? "=> Per-feature cost climbs with cache size: consistent with worse-than-linear (e.g. O(n^2)-shaped) growth."
            : "=> Per-feature cost stays roughly flat: consistent with linear growth in cache size.");
    }

    /// <summary>
    /// The corpus scaling curve above holds the file's own binding count fixed at 64 (CorpusSteps.cs)
    /// and varies workspace-wide cached step count. The real issue report describes the opposite,
    /// dominant axis: a single step-definitions file with ~1,300 methods. StepCodeLensHandler calls
    /// FindUsages once per binding <em>in the requested file</em>, so per-call cost is expected to
    /// scale with bindingsInFile × cachedSteps — this measures the bindingsInFile side directly by
    /// opening a synthetic 1,000-method binding file (all non-matching patterns, so this only adds
    /// scan cost, not additional matches) against the same fully-populated 1,350-step cache, and
    /// compares its per-binding cost to CorpusSteps.cs's (64 bindings) at the same cache size.
    /// </summary>
    [Fact]
    public async Task CodeLens_latency_vs_bindings_in_file()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);
        harness.SendCorpusProjectLoaded(corpusRoot, Path.Combine(corpusRoot, "does-not-exist.dll"));

        // Populate the cache to the corpus's full 1,350 steps.
        var allFeaturePaths = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var version = 1;
        foreach (var path in allFeaturePaths)
            harness.OpenFeature(DocumentUri.FromFileSystemPath(path), version++, File.ReadAllText(path));
        await SettleAsync(harness, allFeaturePaths[^1]);

        // Baseline: CorpusSteps.cs, 64 bindings, against the full cache.
        var corpusCsPath = Path.Combine(corpusRoot, "Bindings", "CorpusSteps.cs");
        var corpusCsUri = DocumentUri.FromFileSystemPath(corpusCsPath);
        harness.OpenCSharp(corpusCsUri, 1, File.ReadAllText(corpusCsPath));
        (await WaitForNonEmptyCodeLensAsync(harness, corpusCsUri)).Should().BeTrue();
        var baselineMs = await MedianCodeLensMsAsync(harness, corpusCsUri);
        const int baselineBindingCount = 64;

        // Large synthetic file: 1,000 non-matching bindings, matching the issue's reported
        // ~1,300-method scale, against the same cache.
        const int largeBindingCount = 1000;
        var largeCsPath = Path.Combine(corpusRoot, "Bindings", "SyntheticLargeFile.cs");
        var largeCsUri = DocumentUri.FromFileSystemPath(largeCsPath);
        harness.OpenCSharp(largeCsUri, 1, BuildLargeBindingFile(largeBindingCount));
        var largeLenses = await WaitForCodeLensCountAsync(harness, largeCsUri, largeBindingCount);
        largeLenses.Should().BeTrue($"expected {largeBindingCount} lenses once Roslyn discovery finishes");
        var largeMs = await MedianCodeLensMsAsync(harness, largeCsUri);

        var baselinePerBinding = baselineMs / baselineBindingCount;
        var largePerBinding = largeMs / largeBindingCount;

        Console.WriteLine("=== Issue #471 FindUsages scaling probe: bindings-in-file axis ===");
        Console.WriteLine($"CorpusSteps.cs   : {baselineBindingCount,5} bindings, {baselineMs,8:F2} ms, {baselinePerBinding,8:F4} ms/binding");
        Console.WriteLine($"SyntheticLarge.cs: {largeBindingCount,5} bindings, {largeMs,8:F2} ms, {largePerBinding,8:F4} ms/binding");
        Console.WriteLine($"Binding-count ratio: {(double)largeBindingCount / baselineBindingCount:F1}x; latency ratio: {largeMs / baselineMs:F1}x");
        Console.WriteLine(largePerBinding > baselinePerBinding * 2
            ? "=> Per-binding cost climbs sharply with bindings-in-file: consistent with worse-than-linear growth on this axis."
            : "=> Per-binding cost stays roughly proportional: consistent with linear growth in bindings-in-file.");
    }

    private static async Task<double> MedianCodeLensMsAsync(BenchmarkLspHarness harness, DocumentUri uri, int samples = 5)
    {
        var timings = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await harness.RequestCodeLensAsync(uri);
            timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        timings.Sort();
        return timings[timings.Count / 2];
    }

    private static async Task<bool> WaitForCodeLensCountAsync(
        BenchmarkLspHarness harness, DocumentUri uri, int minCount, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var lenses = await harness.RequestCodeLensAsync(uri);
            if (lenses is not null && lenses.Length >= minCount) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static string BuildLargeBindingFile(int bindingCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using Reqnroll;");
        sb.AppendLine();
        sb.AppendLine("namespace Benchmark.SyntheticLargeFile;");
        sb.AppendLine();
        sb.AppendLine("[Binding]");
        sb.AppendLine("public class SyntheticLargeFileSteps");
        sb.AppendLine("{");
        for (var i = 0; i < bindingCount; i++)
        {
            sb.AppendLine($"    [When(@\"synthetic non matching pattern number {i} xyz\")]");
            sb.AppendLine($"    public void When_Synthetic{i}() {{ }}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static async Task<bool> WaitForNonEmptyCodeLensAsync(
        BenchmarkLspHarness harness, DocumentUri uri, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var lenses = await harness.RequestCodeLensAsync(uri);
            if (lenses is { Length: > 0 }) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// Waits until the most-recently-opened feature file's semantic tokens are non-empty — the same
    /// "workspace serviceable" signal <c>BatchScenarios.ColdStartScanAsync</c> uses — as a proxy for
    /// "the match-cache write for this batch of opens has landed", before timing anything.
    /// </summary>
    private static async Task SettleAsync(BenchmarkLspHarness harness, string lastOpenedPath, int timeoutMs = 10_000)
    {
        var uri = DocumentUri.FromFileSystemPath(lastOpenedPath);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var tokens = await harness.RequestAsync<OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(
                "textDocument/semanticTokens/full",
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokensParams
                {
                    TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentIdentifier { Uri = uri }
                });
            if (tokens is { Data.Length: > 0 }) return;
            await Task.Delay(50);
        }
    }
}
