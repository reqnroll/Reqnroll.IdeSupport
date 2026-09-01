#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks;

/// <summary>
/// Drives the Performance Verification Layer 2 interactive benchmark suite against the committed
/// corpus and reports per-operation latency percentiles. Absolute performance thresholds are
/// asserted only on a designated
/// reference machine (or with <c>--assert</c>); elsewhere the run is report-only and exits 0.
/// </summary>
public static class BenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var warmup = IntArg(args, "--warmup", 10);
        var measured = IntArg(args, "--iterations", 50);
        // Default to the full committed corpus (50 files, ~1,350 steps): issue #488 found the
        // previous default of 10 undershot the scale (~50 files / ~1,350 steps) that #471's
        // investigation needed to reliably reproduce dispatch-pipeline contention, so routine runs
        // never got close to it.
        var fileCount = IntArg(args, "--files", 50);
        var outPath = StringArg(args, "--out");
        var corpusAssembly = StringArg(args, "--corpus-assembly") ?? CorpusAssemblyLocator.TryFind();
        var includeBatch = !args.Contains("--no-batch");
        var outOfProcess = args.Contains("--out-of-process");
        var serverExe = StringArg(args, "--server-exe") ?? (outOfProcess ? ServerExeLocator.Find() : null);
        var gate = ReferenceMachine.FromEnvironment(args);

        var corpusRoot = CorpusLocator.FindCorpusRoot();
        var manifest = CorpusManifest.Load(CorpusLocator.ManifestPath(corpusRoot));

        Console.WriteLine($"Running interactive benchmarks against corpus at {corpusRoot}");
        Console.WriteLine($"  warmup={warmup} iterations={measured} files={fileCount} " +
                          $"assert={gate.AssertThresholds} out-of-process={outOfProcess}");
        Console.WriteLine(corpusAssembly is not null
            ? $"  corpus bindings assembly: {corpusAssembly}"
            : "  corpus bindings assembly: not found (build Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus " +
              "first to enable the binding-discovery batch scenarios and primed bound-state numbers)");

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
        var scenarios = new InteractiveScenarios(harness, features, warmup, measured);

        if (corpusAssembly is not null)
        {
            // Prime the registry against the built corpus bindings assembly before driving the
            // bound-state benchmarks (definition cache-hit, step completion), so their numbers
            // reflect a populated match cache rather than an empty (unbound) registry.
            harness.SendCorpusProjectLoaded(corpusRoot, corpusAssembly);
            await WaitForBindingDiscoveryAsync(harness, features[0]).ConfigureAwait(false);
        }

        var summaries = new List<(PerfTarget Target, LatencySummary Summary)>
        {
            (PerfTargets.SemanticTokensFull, await scenarios.SemanticTokensAsync().ConfigureAwait(false)),
            (PerfTargets.SemanticTokensDelta, await scenarios.SemanticTokensDeltaAsync().ConfigureAwait(false)),
            (PerfTargets.DocumentSymbol, await scenarios.DocumentSymbolAsync().ConfigureAwait(false)),
            (PerfTargets.FoldingRange, await scenarios.FoldingRangeAsync().ConfigureAwait(false)),
            (PerfTargets.CompletionKeyword, await scenarios.KeywordCompletionAsync().ConfigureAwait(false)),
            (PerfTargets.CompletionStep, await scenarios.StepCompletionAsync().ConfigureAwait(false)),
            (PerfTargets.DefinitionCacheHit, await scenarios.DefinitionAsync().ConfigureAwait(false)),
            (PerfTargets.StepPrepareRename, await scenarios.PrepareRenameAsync().ConfigureAwait(false)),
            (PerfTargets.RenameTargets, await scenarios.RenameTargetsAsync().ConfigureAwait(false)),
            (PerfTargets.FindStepUsages, await scenarios.FindStepUsagesAsync().ConfigureAwait(false)),
            (PerfTargets.StepReferences, await scenarios.StepReferencesAsync().ConfigureAwait(false)),
            (PerfTargets.GoToHooks, await scenarios.GoToHooksAsync().ConfigureAwait(false)),
            (PerfTargets.StepCodeLens, await scenarios.StepCodeLensAsync(corpusRoot).ConfigureAwait(false)),
            (PerfTargets.FeatureHookCodeLens, await scenarios.FeatureHookCodeLensAsync().ConfigureAwait(false)),
            (PerfTargets.HookMatchCountCodeLens, await scenarios.HookMatchCountCodeLensAsync(corpusRoot).ConfigureAwait(false)),
            (PerfTargets.GoToMatchingScenarios, await scenarios.GoToMatchingScenariosAsync(corpusRoot).ConfigureAwait(false)),
            (PerfTargets.ResolveTestTargets, await scenarios.ResolveTestTargetsAsync().ConfigureAwait(false)),
            (PerfTargets.InlayHint, await scenarios.InlayHintAsync().ConfigureAwait(false)),
            (PerfTargets.CodeAction, await scenarios.CodeActionAsync().ConfigureAwait(false)),
            (PerfTargets.DocumentFormatting, await scenarios.DocumentFormattingAsync().ConfigureAwait(false)),
            (PerfTargets.RangeFormatting, await scenarios.RangeFormattingAsync().ConfigureAwait(false)),
            (PerfTargets.OnTypeFormatting, await scenarios.OnTypeFormattingAsync().ConfigureAwait(false)),
            (PerfTargets.PublishDiagnostics, await scenarios.DiagnosticsPushAsync().ConfigureAwait(false)),
            (PerfTargets.CSharpDiagnosticsPush, await scenarios.CSharpDiagnosticsPushAsync(corpusRoot).ConfigureAwait(false)),
        };

        // Batch scenarios (coarse wall-clock). Cold start spins up fresh servers, so it is opt-out
        // via --no-batch for quick interactive-only runs.
        List<(double initMs, double parseMs)>? coldStartPhases = null;
        if (includeBatch)
        {
            Console.WriteLine($"Running batch scenarios (cold-start scan, out-of-process={outOfProcess})...");
            coldStartPhases = new List<(double initMs, double parseMs)>();
            summaries.Add((PerfTargets.ColdStartScan,
                await BatchScenarios.ColdStartScanAsync(
                    corpusRoot, outOfProcess: outOfProcess, serverExePath: serverExe,
                    phases: coldStartPhases).ConfigureAwait(false)));

            Console.WriteLine("Running reconciliation/push batch scenarios (watched-files reconfig, refresh pushes)...");
            summaries.Add((PerfTargets.WatchedFilesReconfig,
                await BatchScenarios.WatchedFilesReconfigAsync(harness, corpusRoot, features[0]).ConfigureAwait(false)));
            summaries.Add((PerfTargets.SemanticTokensRefresh,
                await BatchScenarios.SemanticTokensRefreshAsync(harness, features).ConfigureAwait(false)));
            summaries.Add((PerfTargets.InlayHintRefresh,
                await BatchScenarios.InlayHintRefreshAsync(harness, features).ConfigureAwait(false)));
            summaries.Add((PerfTargets.CodeLensRefresh,
                await BatchScenarios.CodeLensRefreshAsync(harness, corpusRoot, features[0].Text).ConfigureAwait(false)));
        }

        // Binding-discovery batch scenarios: only measurable once a built corpus bindings assembly
        // is available (see Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus). Otherwise reported as
        // skipped rather than measured against an empty registry.
        var skipped = BatchScenarios.UnavailableDiscoveryScenarios(corpusAssembly);
        if (includeBatch && corpusAssembly is not null)
        {
            Console.WriteLine("Running binding-discovery batch scenarios (Roslyn re-discovery, reflection discovery)...");
            summaries.Add((PerfTargets.RoslynReDiscovery,
                await BatchScenarios.RoslynReDiscoveryAsync(harness, corpusRoot, features[0].Uri, features[0].Text)
                    .ConfigureAwait(false)));
            summaries.Add((PerfTargets.ReflectionDiscovery,
                await BatchScenarios.ReflectionDiscoveryAsync(corpusRoot, corpusAssembly)
                    .ConfigureAwait(false)));
            summaries.Add((PerfTargets.CSharpRapidEditBurst,
                await BatchScenarios.CSharpRapidEditBurstAsync(harness, corpusRoot, features[0].Uri, features[0].Text)
                    .ConfigureAwait(false)));

            Console.WriteLine("Running workspace-wide batch scenarios (step rename, find unused step definitions)...");
            summaries.Add((PerfTargets.StepRename,
                await BatchScenarios.StepRenameAsync(harness, features).ConfigureAwait(false)));
            summaries.Add((PerfTargets.FindUnusedStepDefinitions,
                await BatchScenarios.FindUnusedStepDefinitionsAsync(harness).ConfigureAwait(false)));
        }

        // Dispatch-fairness / head-of-line-blocking checks (issue #488, following up on #471/#477;
        // #495 added the second one). Each fires a storm of concurrent activity and races it against
        // a cheap read on a file/range the storm never touches. Needs at least two open files (storm
        // set + probe); skipped below that.
        var contentionChecks = new List<ContentionCheck>();
        if (features.Count >= 2)
        {
            Console.WriteLine("Running dispatch-fairness scenario (many tabs restored / solution reload storm)...");
            var restoredFiles = features.Take(features.Count - 1).ToList();
            var probe = features[^1];
            contentionChecks.Add(await new WorkspaceReloadContentionScenario(
                harness, restoredFiles, probe, new WorkspaceReloadContentionOptions())
                .RunAsync().ConfigureAwait(false));

            Console.WriteLine("Running dispatch-fairness scenario (concurrent Run CodeLens resolveTestTargets callers " +
                               "against one very large feature file, issue #495)...");
            var resolveTestTargetsContention = await ResolveTestTargetsContentionScenario.CreateAsync(
                harness, corpusRoot, probe, new ResolveTestTargetsContentionOptions()).ConfigureAwait(false);
            contentionChecks.Add(await resolveTestTargetsContention.RunAsync().ConfigureAwait(false));

            if (corpusAssembly is not null)
            {
                Console.WriteLine("Running dispatch-fairness scenario (repeated rebuild-refresh projectLoaded " +
                                   "storm, issue #542)...");
                contentionChecks.Add(await new RebuildRefreshContentionScenario(
                    harness, corpusRoot, corpusAssembly, probe, new RebuildRefreshContentionOptions())
                    .RunAsync().ConfigureAwait(false));
            }
        }

        var results = summaries.Select(s => new OperationResult(s.Target, s.Summary)).ToList();
        var report = new BenchmarkReport(
            MachineName: Environment.MachineName,
            AssertThresholds: gate.AssertThresholds,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorpusDescription: $"{manifest.Fingerprint.FeatureFileCount} features, " +
                               $"{manifest.Fingerprint.StepDefinitionPatternCount} patterns, " +
                               $"{manifest.Fingerprint.StepCount} steps",
            Results: results,
            Skipped: skipped,
            Transport: transport,
            ContentionChecks: contentionChecks.Count > 0 ? contentionChecks : null);

        Console.WriteLine();
        Console.WriteLine(report.ToConsoleTable());

        // Phase breakdown for cold-start: shows how the total is split between the LSP initialize
        // handshake (process spawn + CLR bootstrap + our DI init) and the workspace parse phase.
        if (coldStartPhases is { Count: > 0 })
        {
            var avgInit  = coldStartPhases.Average(p => p.initMs);
            var avgParse = coldStartPhases.Average(p => p.parseMs);
            var avgTotal = avgInit + avgParse;
            Console.WriteLine("Cold-start phase breakdown (averages across repetitions):");
            Console.WriteLine($"  Process spawn + CLR boot + initialize handshake : {avgInit,7:F1} ms  ({avgInit / avgTotal * 100,4:F1}%)");
            Console.WriteLine($"  Workspace parse (first tokens serviceable)       : {avgParse,7:F1} ms  ({avgParse / avgTotal * 100,4:F1}%)");
            Console.WriteLine($"  Total                                            : {avgTotal,7:F1} ms");
        }

        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllText(outPath, report.ToJson());
            Console.WriteLine($"Wrote results JSON to {outPath}");
        }

        // Performance gating: fail the process only on a designated reference machine.
        if (gate.AssertThresholds && !report.AllPassed)
        {
            Console.Error.WriteLine("FAIL: one or more performance targets missed on the reference machine.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Polls <c>textDocument/definition</c> on a step known to be bound in the corpus (see
    /// <c>CorpusGenerator</c> — "Given precondition N is met" always binds) until it resolves to a
    /// real location, i.e. the async binding-discovery + match pass triggered by
    /// <c>reqnroll/projectLoaded</c> has completed. Falls through after the timeout so a slow/failed
    /// discovery degrades to the previous (unprimed) behaviour rather than hanging the run.
    /// </summary>
    private static async Task WaitForBindingDiscoveryAsync(BenchmarkLspHarness harness, OpenFeature feature, int timeoutMs = 10_000)
    {
        var (line, character) = feature.StepPosition;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var location = await harness.RequestAsync<LocationOrLocationLinks?>(
                "textDocument/definition",
                new DefinitionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = feature.Uri },
                    Position = new Position(line, character),
                }).ConfigureAwait(false);
            if (location is not null && location.Any()) return;
            await Task.Delay(50).ConfigureAwait(false);
        }
        Console.WriteLine("  warning: binding discovery did not resolve a definition within the timeout; " +
                           "bound-state benchmarks may run against an unprimed registry.");
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
