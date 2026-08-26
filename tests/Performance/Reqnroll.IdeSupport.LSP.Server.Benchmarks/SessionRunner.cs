#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks;

/// <summary>
/// Drives the editing-session ("mixed") benchmark: interactive latency measured under realistic
/// concurrent load — bursts of pipelined requests per edit, a fraction superseded/cancelled, with
/// think-time between bursts. Always report-only (exit 0): the Performance Requirements thresholds
/// are isolated-case, so this is a reality check, not a gate. Use <c>run</c> for the contract check.
/// </summary>
public static class SessionRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var defaults = new SessionOptions();
        var options = defaults with
        {
            Warmup = IntArg(args, "--warmup", defaults.Warmup),
            Bursts = IntArg(args, "--bursts", defaults.Bursts),
            SupersedeRate = DoubleArg(args, "--supersede-rate", defaults.SupersedeRate),
            ThinkMs = IntArg(args, "--think-ms", defaults.ThinkMs),
            TypingGapMs = IntArg(args, "--typing-gap-ms", defaults.TypingGapMs),
            NavigateEveryNthBurst = IntArg(args, "--navigate-every", defaults.NavigateEveryNthBurst),
            CodeLensEveryNthBurst = IntArg(args, "--codelens-every", defaults.CodeLensEveryNthBurst),
        };
        // See BenchmarkRunner's matching change (issue #488): default to the full committed corpus
        // rather than a scale too small to reproduce realistic dispatch contention.
        var fileCount = IntArg(args, "--files", 50);
        var outPath = StringArg(args, "--out");
        var outOfProcess = args.Contains("--out-of-process");
        var serverExe = StringArg(args, "--server-exe") ?? (outOfProcess ? ServerExeLocator.Find() : null);

        var corpusRoot = CorpusLocator.FindCorpusRoot();
        var manifest = CorpusManifest.Load(CorpusLocator.ManifestPath(corpusRoot));

        Console.WriteLine($"Running editing-session benchmark against corpus at {corpusRoot}");
        Console.WriteLine($"  bursts={options.Bursts} warmup={options.Warmup} files={fileCount} " +
                          $"supersede-rate={options.SupersedeRate} think-ms={options.ThinkMs} " +
                          $"typing-gap-ms={options.TypingGapMs} navigate-every={options.NavigateEveryNthBurst} " +
                          $"codelens-every={options.CodeLensEveryNthBurst} out-of-process={outOfProcess}");

        await using var harness = new BenchmarkLspHarness();
        string transport;
        if (outOfProcess)
        {
            Console.WriteLine($"  spawning server exe over stdio: {serverExe}");
            await harness.StartOutOfProcessAsync(corpusRoot, serverExe!).ConfigureAwait(false);
            transport = "out-of-process (spawned exe over stdio)";
        }
        else
        {
            await harness.StartAsync(corpusRoot).ConfigureAwait(false);
            transport = "in-process (in-memory pipe)";
        }

        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, fileCount).ConfigureAwait(false);
        var session = new SessionScenario(harness, features, options);
        var result = await session.RunAsync().ConfigureAwait(false);

        var results = result.Results.Select(r => new OperationResult(r.Target, r.Summary)).ToList();
        var report = new BenchmarkReport(
            MachineName: Environment.MachineName,
            AssertThresholds: false,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorpusDescription: $"{manifest.Fingerprint.FeatureFileCount} features, " +
                               $"{manifest.Fingerprint.StepDefinitionPatternCount} patterns, " +
                               $"{manifest.Fingerprint.StepCount} steps",
            Results: results,
            Skipped: null,
            Session: result.Stats,
            Transport: transport);

        Console.WriteLine();
        Console.WriteLine(report.ToConsoleTable());

        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllText(outPath, report.ToJson());
            Console.WriteLine($"Wrote results JSON to {outPath}");
        }

        return 0;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : fallback;
    }

    private static double DoubleArg(string[] args, string name, double fallback)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length &&
               double.TryParse(args[idx + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
