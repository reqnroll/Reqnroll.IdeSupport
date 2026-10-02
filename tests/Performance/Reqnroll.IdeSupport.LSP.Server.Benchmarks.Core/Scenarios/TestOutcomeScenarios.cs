#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>
/// Identity of a method the harness has genuinely reported an outcome for, so a later
/// <c>reqnroll/testOutcomes/getOutcome</c> finds it — and, because the recorded time is "now", is
/// fresh rather than stale. Produced by <see cref="TestOutcomeScenarios.SeedAsync"/>.
/// </summary>
public sealed record SeededTestOutcome(string AssemblyPath, string TypeFullName, string MethodName);

/// <summary>What <see cref="TestOutcomeScenarios.WriteLargeOutcomeFile"/> wrote, plus a key known to survive its load.</summary>
public sealed record LargeOutcomeFixture(
    string FirstFreshSource, string FirstFreshTypeFullName, string FirstFreshMethodName, int MethodCount);

/// <summary>
/// The test-outcome pipeline scenarios (issue #714, covering #700/#702): driving the server's
/// outcome listener directly over loopback so the whole path — NDJSON ingest, <c>StepTraceParser</c>,
/// the store, the throttled <c>changed</c> push and persistence — is measured without vstest, which
/// is what makes them hermetic and reproducible.
/// </summary>
public static class TestOutcomeScenarios
{
    /// <summary>
    /// How long to wait between ingest steps that must not be absorbed by the listener's throttle
    /// (250ms, <c>TestOutcomeTcpListener.RefreshDebounce</c>) — the same role
    /// <c>ResolveTestTargetsContentionOptions.SettleDelayMs</c> plays for its storm.
    /// </summary>
    public const int RefreshSettleMs = 400;

    /// <summary>The logger's own cap (<c>ReqnrollIdeTestLogger.MaxStdoutLength</c>), used for the worst-case rows.</summary>
    public const int StdoutCapChars = 64 * 1024;

    /// <summary>Scenario E's scale: the issue's "~10k methods across many container paths".</summary>
    public const int LargeMethodCount = 10_000;

    /// <summary>
    /// A key no run has ever reported: the container path does not exist and the type is not one any
    /// generator emits, so <c>getOutcome</c> takes its dictionary-miss path and never reaches the
    /// container stat or the row sort.
    /// </summary>
    public static readonly string UnseededAssemblyPath =
        Path.Combine(Path.GetTempPath(), "reqnroll-benchmark-unseeded", "NeverReportedContainer.dll");

    public const string UnseededTypeFullName = "Benchmark.Unreported.Feature_NotARun";
    public const string UnseededMethodName = "Scenario_0";

    /// <summary>The identity the seed reports a result for.</summary>
    public const string SeededTypeFullName = "Benchmark.OutcomeSeed.PreconditionFeature";
    public const string SeededMethodName = "Scenario_0";

    /// <summary>Why the outcome scenarios that need a real container are not measured on a run without one.</summary>
    private const string NeedsAssemblyReason =
        "requires a built corpus bindings assembly (its path is the seeded result's container, so the " +
        "outcome resolves as fresh rather than as a stale-path artefact)";

    /// <summary>The outcome scenarios that cannot be measured without a built corpus bindings assembly.</summary>
    public static IReadOnlyList<SkippedBatchScenario> UnavailableScenarios(string? corpusAssemblyPath)
        => !string.IsNullOrEmpty(corpusAssemblyPath) && File.Exists(corpusAssemblyPath)
            ? Array.Empty<SkippedBatchScenario>()
            : new[]
            {
                new SkippedBatchScenario(PerfTargets.GetTestOutcome, NeedsAssemblyReason),
                new SkippedBatchScenario(PerfTargets.GetTestOutcomeFound, NeedsAssemblyReason),
            };

    // ── Seeding ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Posts one synthetic run through the listener so the <c>getOutcome</c> scenarios have something
    /// real to find, then waits until the lookup actually resolves — the listener handles the
    /// connection on the thread pool, so the store is not populated the moment the socket closes.
    /// Needs no vstest: <c>registerRun</c> → loopback connect → NDJSON (issue #714, scenario A).
    /// </summary>
    /// <param name="corpusAssemblyPath">
    /// Used as the result's <c>source</c>: a container that genuinely exists on disk, so the
    /// freshness check sees a fresh outcome instead of turning every <c>#found</c> sample into a
    /// stale-path artefact.
    /// </param>
    public static async Task<SeededTestOutcome> SeedAsync(
        BenchmarkLspHarness harness, string corpusAssemblyPath,
        string typeFullName = SeededTypeFullName, string methodName = SeededMethodName,
        int traceSteps = 6, CancellationToken ct = default)
    {
        var registration = await harness.RequestRegisterTestRunAsync(ct).ConfigureAwait(false);
        var endpoint = registration?.Endpoint;
        if (registration is null || !registration.Success || string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException("registerRun did not return an endpoint; cannot seed outcomes.");

        var row = TestOutcomeSeedRow.For(
            corpusAssemblyPath, typeFullName, methodName, stdout: BuildStepTrace(traceSteps, methodName));

        using (var connection = await TestOutcomeSeedConnection.ConnectAsync(endpoint!, ct).ConfigureAwait(false))
        {
            var runId = registration.RunId ?? Guid.NewGuid().ToString("N");
            await connection.WriteHelloAsync(runId, ct).ConfigureAwait(false);
            await connection.WriteRunStartAsync(runId, new[] { row }, ct: ct).ConfigureAwait(false);
            await connection.WriteResultAsync(runId, row, ct).ConfigureAwait(false);
            await connection.WriteRunCompleteAsync(runId, executed: 1, ct).ConfigureAwait(false);
            await connection.CloseAsync(ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var response = await harness
                .RequestGetTestOutcomeAsync(corpusAssemblyPath, typeFullName, methodName, ct).ConfigureAwait(false);
            if (response is { Found: true })
                return new SeededTestOutcome(corpusAssemblyPath, typeFullName, methodName);
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"the seeded outcome for {typeFullName}.{methodName} never became readable through getOutcome.");
    }

    // ── Scenario B: ingest burst ────────────────────────────────────────────────

    /// <summary>
    /// Scenario B: posts <paramref name="resultCount"/> synthetic results over one loopback
    /// connection and measures the wall-clock from <c>runStart</c> to <c>runComplete</c> <em>plus</em>
    /// the listener's trailing <c>Save</c> — the latter observed as the redirected persistence file
    /// being rewritten, since nothing on the wire announces it. Covers the store lock, the
    /// <c>StepTraceParser</c> cost and the throttle interaction, none of which had any coverage.
    /// </summary>
    public static async Task<LatencySummary> IngestBurstAsync(
        BenchmarkLspHarness harness, string persistenceFilePath, string source,
        IReadOnlyList<(int ResultCount, int Repetitions)> scales,
        int largeStdoutEveryNth = 50, CancellationToken ct = default)
    {
        var recorder = new LatencyRecorder(PerfTargets.TestOutcomesIngestBurst.Operation);

        foreach (var (resultCount, repetitions) in scales)
        {
            var rows = BuildBurstRows(source, resultCount, largeStdoutEveryNth);
            var perScale = new LatencyRecorder(PerfTargets.TestOutcomesIngestBurst.Operation);

            for (var rep = 0; rep < repetitions; rep++)
            {
                var registration = await harness.RequestRegisterTestRunAsync(ct).ConfigureAwait(false);
                var endpoint = registration?.Endpoint;
                if (registration is null || !registration.Success || string.IsNullOrEmpty(endpoint))
                    throw new InvalidOperationException("registerRun did not return an endpoint; cannot run the ingest burst.");

                var runId = registration.RunId ?? Guid.NewGuid().ToString("N");
                var writtenBefore = LastWriteUtcOrMin(persistenceFilePath);

                using var connection = await TestOutcomeSeedConnection.ConnectAsync(endpoint!, ct).ConfigureAwait(false);
                var start = Stopwatch.GetTimestamp();
                await connection.WriteHelloAsync(runId, ct).ConfigureAwait(false);
                await connection.WriteRunStartAsync(runId, rows, ct: ct).ConfigureAwait(false);
                foreach (var row in rows)
                    await connection.WriteResultAsync(runId, row, ct).ConfigureAwait(false);
                await connection.WriteRunCompleteAsync(runId, rows.Count, ct).ConfigureAwait(false);

                // Closing the connection is what makes the listener clear the run's marks and Save.
                await connection.CloseAsync(ct).ConfigureAwait(false);
                var saved = await BenchmarkLspHarness
                    .WaitForFileWriteAsync(persistenceFilePath, writtenBefore).ConfigureAwait(false);
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                recorder.Add(ms);
                perScale.Add(ms);

                if (!saved)
                    Console.WriteLine($"  [ingest burst] {resultCount} results, repetition {rep}: the trailing Save was " +
                                       "not observable within the timeout; the sample is a lower bound (flush only).");

                // Let this repetition's throttle window drain before the next one starts writing.
                await Task.Delay(RefreshSettleMs, ct).ConfigureAwait(false);
            }

            // The label is one row in the report, so the per-scale shape (corpus-sized, then the
            // 2,000-scenario stress size) is reported here instead of being folded into it silently.
            var summary = perScale.Summarize();
            Console.WriteLine($"  [ingest burst] {resultCount} results x{repetitions}: " +
                              $"P50={summary.P50Ms:F1}ms P95={summary.P95Ms:F1}ms max={summary.MaxMs:F1}ms");
        }

        return recorder.Summarize();
    }

    // ── Harness plumbing F: the changed push ────────────────────────────────────

    /// <summary>
    /// The client-side cost of staying in sync: post a result, wait for the server-initiated
    /// <c>reqnroll/testOutcomes/changed</c> push it causes. That push is throttled at 250ms rather
    /// than debounced, so the number is dominated by a fixed delay, not by processing cost — hence
    /// the target's <c>IncludesFixedDelay</c> flag. Each repetition tears its run down and drains the
    /// throttle window first, so a late fire from the previous repetition can never be mistaken for
    /// this one's push.
    /// </summary>
    public static async Task<LatencySummary> ChangedPushAsync(
        BenchmarkLspHarness harness, string source, int repetitions = 5, CancellationToken ct = default)
    {
        var recorder = new LatencyRecorder(PerfTargets.TestOutcomesChanged.Operation);

        for (var rep = 0; rep < repetitions; rep++)
        {
            var registration = await harness.RequestRegisterTestRunAsync(ct).ConfigureAwait(false);
            var endpoint = registration?.Endpoint;
            if (registration is null || !registration.Success || string.IsNullOrEmpty(endpoint))
                throw new InvalidOperationException("registerRun did not return an endpoint; cannot time the changed push.");

            var runId = registration.RunId ?? Guid.NewGuid().ToString("N");
            var row = TestOutcomeSeedRow.For(
                source, $"Benchmark.OutcomePush.Feature_{rep:0000}", "Scenario_0", stdout: BuildStepTrace(4));

            using var connection = await TestOutcomeSeedConnection.ConnectAsync(endpoint!, ct).ConfigureAwait(false);
            await connection.WriteHelloAsync(runId, ct).ConfigureAwait(false);
            await connection.WriteRunStartAsync(runId, new[] { row }, ct: ct).ConfigureAwait(false);

            // The push is throttled from the moment the store changes, which is this result's record.
            var start = Stopwatch.GetTimestamp();
            await connection.WriteResultAsync(runId, row, ct).ConfigureAwait(false);
            var ms = await harness.WaitForTestOutcomesChangedAsync(start, timeoutMs: 5000).ConfigureAwait(false);
            if (ms is not null)
                recorder.Add(ms.Value);
            else
                Console.WriteLine($"  [testOutcomes/changed] repetition {rep}: no push arrived within the timeout; sample skipped.");

            await connection.WriteRunCompleteAsync(runId, executed: 1, ct).ConfigureAwait(false);
            await connection.CloseAsync(ct).ConfigureAwait(false);
            await Task.Delay(RefreshSettleMs, ct).ConfigureAwait(false);
        }

        var summary = recorder.Summarize();
        Console.WriteLine($"  [testOutcomes/changed] first result -> push x{repetitions}: " +
                          $"P50={summary.P50Ms:F1}ms P95={summary.P95Ms:F1}ms max={summary.MaxMs:F1}ms " +
                          "(includes the listener's fixed 250ms throttle)");
        return summary;
    }

    // ── Scenario E: persistence scale ───────────────────────────────────────────

    /// <summary>
    /// Scenario E's fixture: a persisted outcome file holding <paramref name="methodCount"/> methods
    /// across <paramref name="containerCount"/> container paths, deliberately mixed — some containers
    /// absent from disk, some "rebuilt" since the outcome was recorded, the rest fresh. <c>Load</c>
    /// stats every persisted container, so that mix is what makes the read cost representative, and
    /// it is where the hermeticity prerequisite is actually exercised.
    /// </summary>
    /// <returns>The file's first fresh key, which a caller uses to prove the load really happened.</returns>
    public static LargeOutcomeFixture WriteLargeOutcomeFile(
        string filePath, string containerDirectory, int methodCount = LargeMethodCount,
        int containerCount = 20, int missingEveryNth = 7, int rebuiltEveryNth = 5)
    {
        const int methodsPerContainer = 500;
        containerCount = Math.Max(containerCount, (methodCount + methodsPerContainer - 1) / methodsPerContainer);
        containerDirectory = Path.GetFullPath(containerDirectory);
        Directory.CreateDirectory(containerDirectory);

        // Write every container first, so a plain write time is already older than the recorded
        // updatedUtc below; the "rebuilt" ones are re-stamped afterwards to become stale.
        var containers = new List<(string Path, bool Missing, bool Rebuilt)>();
        for (var c = 0; c < containerCount; c++)
        {
            var path = Path.Combine(containerDirectory, $"BenchmarkOutcome_{c:000}.dll");
            var missing = c % missingEveryNth == 0;
            if (!missing)
                File.WriteAllText(path, "not a real assembly; the freshness check only stats it");
            containers.Add((path, missing, c % rebuiltEveryNth == 0));
        }

        var updatedUtc = DateTime.UtcNow;
        var methods = new JArray();
        string? firstFreshSource = null;
        string firstFreshType = "";
        var firstFreshMethod = "Scenario_0";

        for (var m = 0; m < methodCount; m++)
        {
            var container = containers[m / methodsPerContainer % containers.Count];
            if (container.Missing) continue;
            var type = $"Benchmark.OutcomeScale.Feature_{m / 10:0000}";
            var method = $"Scenario_{m % 10}";
            methods.Add(new JObject
            {
                ["source"] = container.Path,
                ["type"] = type,
                ["method"] = method,
                ["updatedUtc"] = updatedUtc,
                ["rows"] = new JArray(new JObject
                {
                    ["displayName"] = method,
                    ["outcome"] = "Passed",
                    ["durationMs"] = 12.5,
                    ["errorMessage"] = null,
                    ["runId"] = "fixture",
                    ["recordedUtc"] = updatedUtc,
                    ["steps"] = new JArray(new[]
                    {
                        new JObject { ["text"] = "Given precondition 0 is met", ["outcome"] = "Done", ["detail"] = "steps", ["seconds"] = 0.1 },
                        new JObject { ["text"] = "When I perform action 0", ["outcome"] = "Done", ["detail"] = "steps", ["seconds"] = 0.2 },
                    }),
                }),
            });

            if (firstFreshSource is null && !container.Rebuilt)
            {
                firstFreshSource = container.Path;
                firstFreshType = type;
                firstFreshMethod = method;
            }
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, new JObject
        {
            ["version"] = 1,
            ["methods"] = methods,
        }.ToString());

        // Re-stamp the "rebuilt" containers so their write time is newer than the outcomes recorded
        // above: exactly the shape Load is supposed to prune.
        foreach (var container in containers.Where(c => !c.Missing && c.Rebuilt))
        {
            try { File.SetLastWriteTimeUtc(container.Path, updatedUtc.AddSeconds(30)); } catch (Exception) { }
        }

        return new LargeOutcomeFixture(
            firstFreshSource ?? containers.First(c => !c.Missing).Path,
            firstFreshType,
            firstFreshMethod,
            methods.Count);
    }

    /// <summary>
    /// Scenario E, read half: the session-start cost. A fresh server is started per repetition and
    /// the very first <c>getOutcome</c> is timed — the store and its persistence are singletons
    /// resolved lazily on that first request, so the measured round trip is exactly "dial the
    /// outcome, pay for loading the file".
    /// </summary>
    public static async Task<LatencySummary> PersistenceLoadAsync(
        string corpusRoot, string persistenceFilePath, string containerDirectory,
        int methodCount = LargeMethodCount, int repetitions = 3, CancellationToken ct = default)
    {
        var recorder = new LatencyRecorder(PerfTargets.TestOutcomesPersistenceLoad.Operation);

        for (var rep = 0; rep < repetitions; rep++)
        {
            var fixture = WriteLargeOutcomeFile(persistenceFilePath, containerDirectory, methodCount);

            await using var harness = new BenchmarkLspHarness();
            await harness.StartAsync(corpusRoot).ConfigureAwait(false);

            var start = Stopwatch.GetTimestamp();
            var response = await harness.RequestGetTestOutcomeAsync(
                fixture.FirstFreshSource, fixture.FirstFreshTypeFullName, fixture.FirstFreshMethodName, ct)
                .ConfigureAwait(false);
            recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            if (response is not { Found: true })
                throw new InvalidOperationException(
                    $"the {fixture.MethodCount}-method fixture did not load: its first fresh key was not found. " +
                    DescribeFixtureLookup(persistenceFilePath, fixture));
        }

        var summary = recorder.Summarize();
        Console.WriteLine($"  [persistence load] {methodCount}-method fixture x{repetitions}: " +
                          $"P50={summary.P50Ms:F1}ms P95={summary.P95Ms:F1}ms max={summary.MaxMs:F1}ms");
        return summary;
    }

    /// <summary>
    /// Scenario E, write half: the post-run merge + write. Loading the large file first is what makes
    /// this the real save path (merge into the file on disk, prune, rewrite) rather than a write of
    /// one fresh row; the measured window runs from <c>runComplete</c> to the moment the file
    /// actually changes, since the save is not an LSP message.
    /// </summary>
    public static async Task<LatencySummary> PersistenceSaveAsync(
        string corpusRoot, string persistenceFilePath, string containerDirectory,
        int methodCount = LargeMethodCount, int repetitions = 3, CancellationToken ct = default)
    {
        var recorder = new LatencyRecorder(PerfTargets.TestOutcomesPersistenceSave.Operation);

        for (var rep = 0; rep < repetitions; rep++)
        {
            var fixture = WriteLargeOutcomeFile(persistenceFilePath, containerDirectory, methodCount);

            await using var harness = new BenchmarkLspHarness();
            await harness.StartAsync(corpusRoot).ConfigureAwait(false);

            // Force the load, so the store holds the whole fixture and the save below really merges
            // against it (an unloaded store would write one row into an empty file).
            await harness.RequestGetTestOutcomeAsync(
                fixture.FirstFreshSource, fixture.FirstFreshTypeFullName, fixture.FirstFreshMethodName, ct)
                .ConfigureAwait(false);

            var registration = await harness.RequestRegisterTestRunAsync(ct).ConfigureAwait(false);
            var endpoint = registration?.Endpoint;
            if (registration is null || !registration.Success || string.IsNullOrEmpty(endpoint))
                throw new InvalidOperationException("registerRun did not return an endpoint; cannot time the save.");

            var runId = registration.RunId ?? Guid.NewGuid().ToString("N");
            var row = TestOutcomeSeedRow.For(
                fixture.FirstFreshSource, "Benchmark.OutcomeScale.PersistenceSave", "Scenario_0",
                stdout: BuildStepTrace(4));

            using var connection = await TestOutcomeSeedConnection.ConnectAsync(endpoint!, ct).ConfigureAwait(false);
            await connection.WriteHelloAsync(runId, ct).ConfigureAwait(false);
            await connection.WriteRunStartAsync(runId, new[] { row }, ct: ct).ConfigureAwait(false);
            await connection.WriteResultAsync(runId, row, ct).ConfigureAwait(false);

            var writtenBefore = LastWriteUtcOrMin(persistenceFilePath);
            var start = Stopwatch.GetTimestamp();
            await connection.WriteRunCompleteAsync(runId, executed: 1, ct).ConfigureAwait(false);
            await connection.CloseAsync(ct).ConfigureAwait(false);
            var saved = await BenchmarkLspHarness
                .WaitForFileWriteAsync(persistenceFilePath, writtenBefore).ConfigureAwait(false);
            recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            if (!saved)
                Console.WriteLine($"  [persistence save] repetition {rep}: the save was not observable within " +
                                   "the timeout; the sample is a lower bound (flushed messages only).");
        }

        var summary = recorder.Summarize();
        Console.WriteLine($"  [persistence save] {methodCount}-method fixture x{repetitions}: " +
                          $"P50={summary.P50Ms:F1}ms P95={summary.P95Ms:F1}ms max={summary.MaxMs:F1}ms");
        return summary;
    }

    // ── Fixture shapes ──────────────────────────────────────────────────────────

    /// <summary>
    /// A realistic Reqnroll step trace: the step line, then its <c>-&gt; </c>-prefixed outcome, which
    /// is the pair <c>StepTraceParser</c> actually parses out of captured stdout. Ingest scenarios use
    /// this instead of an empty string so the parser's cost is real (it is one of the pipeline's
    /// per-row costs, and it runs under the store's ingest path).
    /// </summary>
    public static string BuildStepTrace(int steps, string bindingClass = "CorpusSteps")
    {
        // The parser only accepts a candidate step line that starts with a Gherkin keyword in some
        // language; "Given " is the corpus's own first-step shape.
        var keywords = new[] { "Given", "When", "Then" };
        var sb = new StringBuilder();
        for (var i = 0; i < steps; i++)
        {
            var keyword = keywords[i % keywords.Length];
            sb.Append(keyword).Append(" synthetic step ").Append(i).Append(" runs\n");
            sb.Append("-> done: ").Append(bindingClass).Append(".Step").Append(i).Append(" (")
              .Append((0.05 + i * 0.01).ToString("0.00", CultureInfo.InvariantCulture)).Append("s)\n");
        }
        return sb.ToString();
    }

    /// <summary>A row's output at (a little over) the logger's 64 KB capture cap — the worst case for the parser.</summary>
    public static string BuildLargeStepTrace(int targetChars)
    {
        var sb = new StringBuilder(targetChars + 128);
        var i = 0;
        while (sb.Length < targetChars)
        {
            sb.Append("Given a very verbose step ").Append(i).Append(" is met\n");
            sb.Append("-> done: VerboseSteps.Step").Append(i).Append(" (12.34s)\n");
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The burst's rows, shaped like a real suite: distinct generated methods in one container, each
    /// with a short multi-step trace, and every <paramref name="largeStdoutEveryNth"/>-th one carrying
    /// output at the logger's 64 KB cap, so the parser is exercised at its worst case and not only
    /// its typical one.
    /// </summary>
    public static IReadOnlyList<TestOutcomeSeedRow> BuildBurstRows(string source, int count, int largeStdoutEveryNth)
    {
        var rows = new List<TestOutcomeSeedRow>(count);
        for (var i = 0; i < count; i++)
        {
            var type = $"Benchmark.OutcomeIngest.Feature_{i / 10:0000}";
            var method = $"Scenario_{i % 10}";
            var large = largeStdoutEveryNth > 0 && i > 0 && i % largeStdoutEveryNth == 0;
            rows.Add(TestOutcomeSeedRow.For(
                source, type, method,
                outcome: i % 25 == 0 ? "Failed" : "Passed",
                durationMs: 5 + i % 40,
                stdout: large ? BuildLargeStepTrace(StdoutCapChars) : BuildStepTrace(6),
                stdoutTruncated: large));
        }
        return rows;
    }

    /// <summary>
    /// Why a fixture lookup failed, in the terms that actually distinguish the causes: is the file on
    /// disk at all, does it still hold the entry, and — the decisive one — does the product's own
    /// loader find the key when it reads that same file in this process? A "no" there means the
    /// fixture is wrong; a "yes" means the server was reading a different file (or a store that had
    /// already been constructed), which is a hermeticity problem rather than a fixture one.
    /// </summary>
    private static string DescribeFixtureLookup(string filePath, LargeOutcomeFixture fixture)
    {
        var report = new StringBuilder();
        try
        {
            report.Append($"file={filePath} exists={File.Exists(filePath)}");
            if (File.Exists(filePath))
            {
                var methods = JObject.Parse(File.ReadAllText(filePath))["methods"] as JArray;
                report.Append($", entries={methods?.Count ?? -1}");
                var entry = methods?.OfType<JObject>().FirstOrDefault(m =>
                    (string?)m["type"] == fixture.FirstFreshTypeFullName && (string?)m["method"] == fixture.FirstFreshMethodName);
                report.Append($", target-entry-present={entry is not null}");
                if (entry is not null)
                    report.Append($", container-exists={File.Exists((string?)entry["source"])}, updatedUtc={entry["updatedUtc"]}");
            }
        }
        catch (Exception ex)
        {
            report.Append($", inspecting the file threw {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var persistence = new TestOutcomePersistence(new IdeSupportNullLogger());
            var loaded = persistence.Load();
            var key = TestOutcomeKey.ForLookup(
                fixture.FirstFreshSource, fixture.FirstFreshTypeFullName, fixture.FirstFreshMethodName);
            var store = new TestOutcomeStore(persistence);
            report.Append($" || same-process loader: resolvedPath={persistence.FilePath}, loaded={loaded.Count}, " +
                          $"containsKey={loaded.Any(o => TestOutcomeKey.Comparer.Equals(o.Key, key))}, " +
                          $"store.TryGet={(store.TryGet(key) is null ? "null" : "found")}");
        }
        catch (Exception ex)
        {
            report.Append($" || same-process loader threw {ex.GetType().Name}: {ex.Message}");
        }

        return report.ToString();
    }

    private static DateTime LastWriteUtcOrMin(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception) { return DateTime.MinValue; }
    }
}