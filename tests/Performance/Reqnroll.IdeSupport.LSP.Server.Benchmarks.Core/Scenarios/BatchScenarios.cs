#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>
/// The batch / throughput performance scenarios, confirmed with wall-clock timing (these targets are coarse
/// enough not to need protocol-boundary percentiles). Cold start is measured by spinning up fresh
/// in-process servers; the binding-discovery scenarios are measured only when a built corpus
/// assembly is supplied (otherwise they are reported as skipped, never faked).
/// </summary>
public static class BatchScenarios
{
    /// <summary>
    /// Cold-start scan: for each repetition, start a fresh server, complete the initialize
    /// handshake, open every corpus feature file, and wait until the first file yields semantic
    /// tokens — i.e. the workspace is parsed and serviceable. Reports the wall-clock distribution.
    /// </summary>
    /// <param name="phases">
    /// When non-null, per-repetition phase timings are appended: <c>initMs</c> is the time from
    /// exe-spawn (or stream-pair creation) through the LSP initialize handshake; <c>parseMs</c> is
    /// the additional time until the first semantic-tokens response is non-empty.
    /// </param>
    public static async Task<LatencySummary> ColdStartScanAsync(
        string corpusRoot, int repetitions = 3, bool outOfProcess = false, string? serverExePath = null,
        List<(double initMs, double parseMs)>? phases = null)
    {
        var recorder = new LatencyRecorder(PerfTargets.ColdStartScan.Operation);
        var exe = outOfProcess ? (serverExePath ?? ServerExeLocator.Find()) : null;
        var featurePaths = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        for (var rep = 0; rep < repetitions; rep++)
        {
            // Out-of-process cold start includes the real cost of launching the server exe.
            var start = Stopwatch.GetTimestamp();

            await using var harness = new BenchmarkLspHarness();
            if (outOfProcess)
                await harness.StartOutOfProcessAsync(corpusRoot, exe!).ConfigureAwait(false);
            else
                await harness.StartAsync(corpusRoot).ConfigureAwait(false);

            // Phase A ends here: process spawned + CLR bootstrapped + LSP initialize handshake done.
            var initMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            DocumentUri? firstUri = null;
            foreach (var path in featurePaths)
            {
                var uri = DocumentUri.FromFileSystemPath(path);
                firstUri ??= uri;
                harness.OpenFeature(uri, 1, File.ReadAllText(path));
            }

            // Wait until the workspace is serviceable (first file parsed).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var tokens = await harness.RequestAsync<SemanticTokens?>(
                    "textDocument/semanticTokens/full",
                    new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = firstUri! } })
                    .ConfigureAwait(false);
                if (tokens is { Data.Length: > 0 }) break;
                await Task.Delay(25).ConfigureAwait(false);
            }

            var totalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // Phase B: time spent after the initialize handshake until the workspace was serviceable.
            phases?.Add((initMs, totalMs - initMs));
            recorder.Add(totalMs);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// The binding-discovery batch scenarios (Roslyn single-file re-discovery; reflection post-build
    /// discovery) require a <b>built</b> corpus assembly + the connector, which the committed
    /// source-only corpus does not include on its own (see
    /// <c>Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus</c>, built and deployed next to the
    /// benchmark). When it is not available, these are reported as skipped rather than measured
    /// against an empty registry.
    /// </summary>
    public static IReadOnlyList<SkippedBatchScenario> UnavailableDiscoveryScenarios(string? corpusAssemblyPath)
    {
        if (!string.IsNullOrEmpty(corpusAssemblyPath) && File.Exists(corpusAssemblyPath))
            return Array.Empty<SkippedBatchScenario>();

        const string reason = "requires a built corpus bindings assembly (not part of the source-only corpus)";
        return new[]
        {
            new SkippedBatchScenario(PerfTargets.RoslynReDiscovery, reason),
            new SkippedBatchScenario(PerfTargets.ReflectionDiscovery, reason),
            new SkippedBatchScenario(PerfTargets.StepRename, reason),
            new SkippedBatchScenario(PerfTargets.FindUnusedStepDefinitions, reason),
            new SkippedBatchScenario(PerfTargets.CSharpRapidEditBurst, reason),
        };
    }

    /// <summary>
    /// Step rename, workspace-wide (issue #119): renames the shared "precondition N is met" step
    /// pattern — bound once, referenced by every generated feature file (see
    /// <c>CorpusGenerator.BuildFeature</c>) — the highest-blast-radius rename case, where the
    /// resulting <c>WorkspaceEdit</c> touches every open feature file at once. Coarse wall-clock,
    /// like the other workspace-wide batch scenarios.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Repetitions are idempotent</b> because the harness never applies the returned edit and
    /// never sends <c>reqnroll/renameApplied</c>, so the server discards the registry/match-cache
    /// updates it staged for each rename (issue #671, R3) and every repetition starts from the
    /// same state.
    /// </para>
    /// <para>
    /// That was <i>not</i> true before #671. The server committed those updates unconditionally
    /// for any non-Visual-Studio client — the harness included — so each repetition patched the
    /// registry with the new <c>.cs</c> text while the corpus files on disk still held the old
    /// step, exactly the desync issue #670 reported. Repetitions after the first were therefore
    /// measuring a rename against a registry that no longer matched the corpus, not the same
    /// operation as the first. Expect this number to have risen when #671 landed: it is the
    /// scenario starting to measure five real renames rather than one.
    /// </para>
    /// <para>
    /// <b>Still narrower than a real client's rename.</b> The registry refresh the edit implies (a
    /// full Roslyn parse plus registry patch) now runs on the confirmation path this scenario
    /// deliberately does not exercise, so it falls outside this measurement — see
    /// <c>RenamePostApplyCoordinator</c>. In the field that cost is recorded separately, as
    /// <c>reqnroll/renameApplied</c> or <c>internal/renamePostResponseApply</c>.
    /// </para>
    /// </remarks>
    public static async Task<LatencySummary> StepRenameAsync(
        BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> features, int repetitions = 5)
    {
        var recorder = new LatencyRecorder(PerfTargets.StepRename.Operation);
        for (var rep = 0; rep < repetitions; rep++)
        {
            var f = features[rep % features.Count];
            var (line, character) = f.StepPosition;
            var start = Stopwatch.GetTimestamp();
            await harness.RequestRenameAsync(f.Uri, line, character, $"precondition renamed {rep} is met")
                .ConfigureAwait(false);
            recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return recorder.Summarize();
    }

    /// <summary>
    /// Rename's confirmed-apply commit (issue #671, R3): the registry/match-cache work a rename
    /// implies moved off the measured <c>textDocument/rename</c> round trip onto the client's
    /// <c>reqnroll/renameApplied</c> confirmation, so <see cref="StepRenameAsync"/> above no longer
    /// measures it at all — its own remarks record why. This scenario measures that other half: a
    /// full Roslyn parse of the renamed <c>.cs</c> text, the registry patch, and the reparse
    /// cascade it triggers for every open <c>.feature</c> file (<c>BindingRegistryChangedHandler
    /// .ReparseOpenFilesAsync</c>), timed end to end via the resulting <c>publishDiagnostics</c>
    /// push — the same "trigger a notification with no response, wait for its observable side
    /// effect" pattern <see cref="WatchedFilesReconfigAsync"/> uses for the same reason
    /// (<c>renameApplied</c> is also a notification).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appends to an already-open corpus feature rather than opening a brand-new one, and
    /// updates the <c>.cs</c> binding via <c>ChangeCSharp</c>, never a repeated
    /// <c>OpenCSharp</c>.</b> The second point matters on its own: reissuing <c>didOpen</c> for
    /// the same path every repetition (each with a new class name, matching
    /// <see cref="CSharpRapidEditBurstAsync"/>'s superficial shape but not its actual technique)
    /// silently rediscovers nothing past the first repetition —
    /// <c>CSharpBindingDiscoveryService.UpdateFromSourceAsync</c>'s didOpen skip-check treats a
    /// path that already has any binding as already reconciled and never reparses it, so every
    /// later repetition's edit is dropped and the registry keeps the first one's binding
    /// indefinitely (confirmed live: <c>reqnroll/findUnusedStepDefinitions</c> showed the first
    /// repetition's class registered and every later one absent, not merely stale). A
    /// <c>didChange</c> carries no such check, which is exactly the "placeholder open, then real
    /// edits via Change" shape <see cref="CSharpRapidEditBurstAsync"/> actually uses and this
    /// scenario now mirrors precisely rather than approximately.
    /// </para>
    /// <para>
    /// Reusing an already-open <paramref name="host"/> feature (rather than a brand-new one) is a
    /// second, independent choice: this scenario needs to trigger a real reparse of an open
    /// <c>.feature</c> file to measure, so it needs one already flowing through the same ordinary
    /// edit pipeline every other scenario here also depends on, rather than establishing a new
    /// file's scope/ownership from nothing.
    /// </para>
    /// <para>
    /// <b>Still safe against corrupting the shared corpus.</b> Only the appended block uses this
    /// repetition's fresh, uniquely-named binding; <paramref name="host"/>'s own pre-existing
    /// scenarios/steps — including whatever binding <see cref="StepRenameAsync"/> or the
    /// interactive scenarios exercise against it — are never touched. The host's exact original
    /// text is restored once after the loop, so a scenario running later that reuses
    /// <paramref name="host"/> (its own captured <c>.Text</c>, not the live server-side content)
    /// finds the server back in the state it expects.
    /// </para>
    /// <para>
    /// The confirmed rename is never written back into the appended block's own text — only the
    /// registry is updated to match it (a real desync between live text and registry, the same
    /// kind #670 reported for a genuine client). Harmless here: nothing reads this block again
    /// after its repetition's diagnostics push lands, and the whole block is discarded by the
    /// final restore regardless. The desync exists only to give
    /// <c>BindingRegistryChangedHandler</c> real reparse work to do.
    /// </para>
    /// </remarks>
    public static async Task<LatencySummary> RenameApplyCommitAsync(
        BenchmarkLspHarness harness, string corpusRoot, OpenFeature host,
        int repetitions = 5, int timeoutMs = 5000)
    {
        var recorder = new LatencyRecorder(PerfTargets.RenameApplyCommit.Operation);
        var csUri = DocumentUri.FromFileSystemPath(
            Path.Combine(corpusRoot, "Bindings", "BenchmarkRenameApplyCommit.cs"));

        // Trimmed and re-joined with an explicit separator so the appended block's line numbers
        // are exact regardless of whether the captured host.Text happened to end with its own
        // trailing newline (Split('\n') on text that already ends in "\n" yields one extra empty
        // trailing entry, which silently shifts every computed position below by one).
        var normalizedHostText = host.Text.Replace("\r\n", "\n").TrimEnd('\n');
        var hostLineCount = normalizedHostText.Split('\n').Length;
        // hostLineCount content lines (0..hostLineCount-1), then one blank separator line, then
        // "Scenario: ...", then the step itself — every repetition's block has this same shape.
        var stepLine = hostLineCount + 2;
        const int stepChar = 5; // "When " precedes the step text on its line -- must match
                                 // BindingSource's hardcoded [When(...)] attribute below: Reqnroll
                                 // matches step type strictly, so a "Given" step here would never
                                 // bind to it (confirmed live: registered correctly, reported
                                 // "unused" every repetition).
        var featureVersion = 500;

        // A neutral first didOpen, matching CSharpRapidEditBurstAsync's own placeholder-then-edit
        // shape: CSharpBindingDiscoveryService.UpdateFromSourceAsync's didOpen skip-check treats a
        // repeated didOpen for a path that already has bindings as redundant and does not
        // reparse — confirmed live, every repetition past the first silently kept the prior one's
        // binding once this scenario tried reusing OpenCSharp per repetition instead. Each
        // repetition's real binding is therefore registered via ChangeCSharp (a didChange, which
        // carries no such skip) rather than by reopening.
        harness.OpenCSharp(csUri, 1, BindingSource("RenameApplyCommitPlaceholder", "no match at all"));
        await Task.Delay(200).ConfigureAwait(false); // let the no-op open settle before the first real edit

        for (var rep = 0; rep < repetitions; rep++)
        {
            var stepText = $"rename apply commit {rep} is met";
            var className = "RenameApplyCommit" + rep;
            harness.ChangeCSharp(csUri, 2 + rep, BindingSource(className, Regex.Escape(stepText)));

            featureVersion++;
            harness.ChangeFeature(host.Uri, featureVersion,
                normalizedHostText + $"\n\nScenario: RenameApplyCommit{rep}\n    When {stepText}\n");

            // Readiness: reqnroll/renameTargets reads the match cache directly, so a non-empty
            // response is the most direct confirmation this repetition's fresh binding has both
            // been syntax-discovered and reflected in a reparse of the host — matching
            // PollDefinitionAsync's role for the sibling .cs scenarios above, just against the
            // request this scenario actually needs to succeed.
            var ready = await PollAsync(
                () => harness.RequestRenameTargetsAsync(host.Uri, stepLine, stepChar),
                r => r is { Targets.Count: > 0 }, timeoutMs).ConfigureAwait(false);
            if (!ready) continue;

            var start = Stopwatch.GetTimestamp();
            WorkspaceEdit? edit;
            try
            {
                edit = await harness.RequestRenameAsync(host.Uri, stepLine, stepChar, stepText + " RENAMED")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A miss surfaces as a real JSON-RPC error ("No step definition found at this
                // position" — RenameHandler.HandleRenameAsync's RenameFailedError), not a null
                // result, unlike the readiness poll above. Same tolerance as every other batch
                // scenario's occasional miss: skip this repetition rather than fail the run.
                continue;
            }
            if (edit is null) continue;

            harness.SendRenameApplied(host.Uri, applied: true);

            var ms = await harness.WaitForDiagnosticsAsync(host.Uri, start, timeoutMs).ConfigureAwait(false);
            if (ms is not null) recorder.Add(ms.Value);
        }

        // Restore the host exactly, so a scenario that runs later and reuses it (its own captured
        // .Text, e.g. StepRenameAsync's rotation through `features`) finds the server matching
        // what it expects rather than this scenario's leftover scratch content.
        harness.ChangeFeature(host.Uri, featureVersion + 1, host.Text);

        return recorder.Summarize();
    }

    private static async Task<bool> PollAsync<T>(
        Func<Task<T>> poll, Func<T, bool> isReady, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (isReady(await poll().ConfigureAwait(false))) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Find unused step definitions, workspace-wide (issue #119): a full scan comparing every
    /// step-definition binding against every step usage across the corpus. Coarse wall-clock, like
    /// the other workspace-wide batch scenarios.
    /// </summary>
    public static async Task<LatencySummary> FindUnusedStepDefinitionsAsync(
        BenchmarkLspHarness harness, int repetitions = 5)
    {
        var recorder = new LatencyRecorder(PerfTargets.FindUnusedStepDefinitions.Operation);
        for (var rep = 0; rep < repetitions; rep++)
        {
            var start = Stopwatch.GetTimestamp();
            await harness.RequestFindUnusedStepDefinitionsAsync().ConfigureAwait(false);
            recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return recorder.Summarize();
    }

    /// <summary>
    /// <c>reqnroll.json</c> change reaction, workspace-wide (issue #119 follow-up): simulates the
    /// client's file-watcher reporting a config change (<c>workspace/didChangeWatchedFiles</c>),
    /// which drives <c>WatchedFilesHandler</c> → <c>ReqnrollConfigChangedHandler</c> → per-file
    /// re-diagnose. There's no direct request/response for this — the client only ever sends the
    /// notification — so this waits for the resulting <c>publishDiagnostics</c> push on the first
    /// open feature as the completion signal, the same "trigger, then wait for the observable
    /// side-effect" pattern <c>InteractiveScenarios.DiagnosticsPushAsync</c> uses for edits.
    /// <c>BindingRegistryChangedHandler</c> and <c>FeatureRescanDebouncer</c> sit downstream of this
    /// same reconciliation pipeline and have no protocol identity of their own to time separately —
    /// their cost is folded into this number (and into the Roslyn/reflection discovery scenarios,
    /// which exercise the same pipeline from a different trigger).
    /// </summary>
    public static async Task<LatencySummary> WatchedFilesReconfigAsync(
        BenchmarkLspHarness harness, string corpusRoot, OpenFeature signalFeature, int repetitions = 5)
    {
        var recorder = new LatencyRecorder(PerfTargets.WatchedFilesReconfig.Operation);
        var configUri = DocumentUri.FromFileSystemPath(Path.Combine(corpusRoot, "reqnroll.json"));

        for (var rep = 0; rep < repetitions; rep++)
        {
            var start = Stopwatch.GetTimestamp();
            harness.SendConfigFileChanged(configUri);
            var ms = await harness.WaitForDiagnosticsAsync(signalFeature.Uri, start).ConfigureAwait(false);
            if (ms is not null) recorder.Add(ms.Value);
        }

        return recorder.Summarize();
    }

    // Both refresh handlers debounce match-cache changes into a single request 500ms after the
    // last one. A settle delay longer than that debounce, taken before each repetition's own edit,
    // drains any refresh still in flight from an earlier scenario (e.g. WatchedFilesReconfigAsync's
    // own edits) — otherwise that leftover request can land just after this repetition's "start" and
    // read as an implausibly fast sample.
    private static readonly TimeSpan RefreshSettleDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Server-initiated <c>workspace/semanticTokens/refresh</c> push (issue #119 follow-up):
    /// <c>SemanticTokensRefreshHandler</c> only fires when the client advertised
    /// <c>workspace.semanticTokens.refreshSupport</c> (declared by
    /// <see cref="BenchmarkLspHarness"/>'s client capabilities); edits a feature and waits for the
    /// debounced (500ms) refresh request, so the measured number is dominated by that fixed window —
    /// same honesty as the diagnostics-push target's "from end of debounce" phrasing.
    /// </summary>
    public static async Task<LatencySummary> SemanticTokensRefreshAsync(
        BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> features, int repetitions = 3)
    {
        var recorder = new LatencyRecorder(PerfTargets.SemanticTokensRefresh.Operation);
        // High starting version: these scenarios reuse the same feature URIs already edited by
        // earlier interactive scenarios (DiagnosticsPushAsync etc.), so this must stay clear of
        // whatever version count --iterations produced there.
        var version = 1_000_000;

        for (var rep = 0; rep < repetitions; rep++)
        {
            await Task.Delay(RefreshSettleDelay).ConfigureAwait(false);
            var f = features[rep % features.Count];
            var start = Stopwatch.GetTimestamp();
            harness.ChangeFeature(f.Uri, version++, f.Text + $"\n  # refresh-probe edit {version}\n");
            var ms = await harness.WaitForSemanticTokensRefreshAsync(start).ConfigureAwait(false);
            if (ms is not null) recorder.Add(ms.Value);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// Server-initiated <c>workspace/inlayHint/refresh</c> push (issue #119 follow-up). Mirrors
    /// <see cref="SemanticTokensRefreshAsync"/> for <c>InlayHintRefreshHandler</c>.
    /// </summary>
    public static async Task<LatencySummary> InlayHintRefreshAsync(
        BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> features, int repetitions = 3)
    {
        var recorder = new LatencyRecorder(PerfTargets.InlayHintRefresh.Operation);
        var version = 2_000_000;

        for (var rep = 0; rep < repetitions; rep++)
        {
            await Task.Delay(RefreshSettleDelay).ConfigureAwait(false);
            var f = features[rep % features.Count];
            var start = Stopwatch.GetTimestamp();
            harness.ChangeFeature(f.Uri, version++, f.Text + $"\n  # refresh-probe edit {version}\n");
            var ms = await harness.WaitForInlayHintRefreshAsync(start).ConfigureAwait(false);
            if (ms is not null) recorder.Add(ms.Value);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// Server-initiated <c>workspace/codeLens/refresh</c> push (issue #256 — the other two refresh
    /// pushes above were benchmarked, this one wasn't). Unlike them, it isn't triggered by a plain
    /// <c>.feature</c> edit: <c>BindingRegistryChangedHandler</c> only requests it after an
    /// incremental Roslyn patch that changes a binding's matched expression (see
    /// <see cref="RoslynReDiscoveryAsync"/>'s identical edit shape), so this opens a synthetic
    /// <c>.cs</c> binding file with a non-matching pattern, then edits it to match one of the
    /// corpus's deliberately-unbound steps, and times from that edit to the refresh push.
    /// </summary>
    public static async Task<LatencySummary> CodeLensRefreshAsync(
        BenchmarkLspHarness harness, string corpusRoot, string featureText, int repetitions = 3)
    {
        var recorder = new LatencyRecorder(PerfTargets.CodeLensRefresh.Operation);
        var csUri = DocumentUri.FromFileSystemPath(
            Path.Combine(corpusRoot, "Bindings", "BenchmarkCodeLensRefresh.cs"));
        var undefinedSteps = FindUndefinedStepPositions(featureText);

        for (var rep = 0; rep < Math.Min(repetitions, undefinedSteps.Count); rep++)
        {
            // Unlike the plain .feature edit behind Semantic/InlayHintRefreshAsync, a single
            // BindingRegistryChanged notification here races two independent 500ms debounce
            // timers (BindingRegistryChangedHandler's rescan debouncer and
            // CodeLensRefreshHandler's own match-cache-notification debounce), both eventually
            // sending the same workspace/codeLens/refresh. Without draining them first, a
            // leftover refresh from the prior rep (or from the no-op open below) can land inside
            // this rep's wait window and read as an implausibly fast sample — same rationale as
            // RefreshSettleDelay above.
            await Task.Delay(RefreshSettleDelay).ConfigureAwait(false);

            var (_, _, stepText) = undefinedSteps[rep];
            harness.OpenCSharp(csUri, 1, BindingSource("NonMatchingCodeLens" + rep, "no match at all " + rep));
            await Task.Delay(200).ConfigureAwait(false); // let the no-op open settle before timing the edit

            var start = Stopwatch.GetTimestamp();
            harness.ChangeCSharp(csUri, 2, BindingSource("MatchingCodeLens" + rep, Regex.Escape(stepText)));

            var ms = await harness.WaitForCodeLensRefreshAsync(start).ConfigureAwait(false);
            if (ms is not null) recorder.Add(ms.Value);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// Roslyn single-file re-discovery: open a synthetic <c>.cs</c> binding document (not part of
    /// the corpus tree) with a pattern that doesn't match anything, then edit it in place to match
    /// one of the corpus's deliberately-unbound steps ("undefined step F-S occurs" — see
    /// <c>CorpusGenerator.BuildFeature</c>) — the F2 live-editing path (source-level re-discovery, no
    /// build). Measures wall-clock from the <c>didChange</c> to the moment
    /// <c>textDocument/definition</c> on that step resolves to a real location, i.e. the previously
    /// unbound step becomes bound.
    /// </summary>
    public static async Task<LatencySummary> RoslynReDiscoveryAsync(
        BenchmarkLspHarness harness, string corpusRoot, DocumentUri featureUri, string featureText,
        int repetitions = 3, int timeoutMs = 2500)
    {
        var recorder = new LatencyRecorder(PerfTargets.RoslynReDiscovery.Operation);
        var csUri = DocumentUri.FromFileSystemPath(
            Path.Combine(corpusRoot, "Bindings", "BenchmarkRoslynReDiscovery.cs"));
        var undefinedSteps = FindUndefinedStepPositions(featureText);

        for (var rep = 0; rep < Math.Min(repetitions, undefinedSteps.Count); rep++)
        {
            var (line, character, stepText) = undefinedSteps[rep];
            harness.OpenCSharp(csUri, 1, BindingSource("NonMatching" + rep, "no match at all " + rep));
            await Task.Delay(200).ConfigureAwait(false); // let the no-op open settle before timing the edit

            var start = Stopwatch.GetTimestamp();
            harness.ChangeCSharp(csUri, 2, BindingSource("Matching" + rep, Regex.Escape(stepText)));

            var resolved = await PollDefinitionAsync(harness, featureUri, line, character, timeoutMs)
                .ConfigureAwait(false);
            if (resolved) recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return recorder.Summarize();
    }

    // Finds every "When undefined step ... occurs" line (the corpus's deliberately-unbound steps —
    // see CorpusGenerator.BuildFeature) and returns a cursor position partway into the step text
    // plus the exact step text (minus the leading keyword) to bind against.
    private static List<(int Line, int Character, string StepText)> FindUndefinedStepPositions(string featureText)
    {
        var results = new List<(int, int, string)>();
        var lines = featureText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            const string prefix = "When undefined step ";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var indent = lines[i].Length - trimmed.Length;
            var stepText = trimmed["When ".Length..];
            results.Add((i, indent + "When undef".Length, stepText));
        }
        return results;
    }

    /// <summary>
    /// Regression guard for the staleness short-circuit in
    /// <c>CSharpBindingDiscoveryService.UpdateFromSourceAsync</c> (issue #531): fires a rapid burst of
    /// superseded <c>.cs</c> edits — no delay between them, faster than Roslyn can parse a single one,
    /// modelling fast typing — against a synthetic binding file, then a final edit that actually
    /// matches one of the corpus's deliberately-unbound steps, and measures wall-clock from that FINAL
    /// edit to the step resolving. <c>IParseCoordinator</c> chains every queued call rather than
    /// cancelling superseded ones (by design), so before #531 every junk edit in the burst still did a
    /// full, uncancellable Roslyn re-parse + registry patch; the short-circuit instead compares each
    /// queued call's text against the current cache entry and skips the parse once a later edit has
    /// already overwritten it. A regression that reintroduces the uncancellable pile-up should show up
    /// here as this number growing with <paramref name="burstSize"/> instead of staying close to
    /// <see cref="RoslynReDiscoveryAsync"/>'s single-edit number.
    /// </summary>
    public static async Task<LatencySummary> CSharpRapidEditBurstAsync(
        BenchmarkLspHarness harness, string corpusRoot, DocumentUri featureUri, string featureText,
        int repetitions = 3, int burstSize = 8, int timeoutMs = 2500)
    {
        var recorder = new LatencyRecorder(PerfTargets.CSharpRapidEditBurst.Operation);
        var csUri = DocumentUri.FromFileSystemPath(
            Path.Combine(corpusRoot, "Bindings", "BenchmarkRapidEditBurst.cs"));
        var undefinedSteps = FindUndefinedStepPositions(featureText);

        for (var rep = 0; rep < Math.Min(repetitions, undefinedSteps.Count); rep++)
        {
            var (line, character, stepText) = undefinedSteps[rep];
            harness.OpenCSharp(csUri, 1, BindingSource("NonMatchingBurst" + rep, "no match at all " + rep));
            await Task.Delay(200).ConfigureAwait(false); // let the no-op open settle before the burst

            // The burst: several superseded edits fired back to back with no delay between them, so
            // each queued call's cached text is already stale by the time UpdateFromSourceAsync would
            // get to it — exactly the shape the short-circuit exists to skip.
            for (var i = 0; i < burstSize; i++)
                harness.ChangeCSharp(csUri, 2 + i,
                    BindingSource($"BurstJunk{rep}_{i}", $"still no match {rep}_{i}"));

            var start = Stopwatch.GetTimestamp();
            harness.ChangeCSharp(csUri, 2 + burstSize, BindingSource("BurstFinal" + rep, Regex.Escape(stepText)));

            var resolved = await PollDefinitionAsync(harness, featureUri, line, character, timeoutMs)
                .ConfigureAwait(false);
            if (resolved) recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return recorder.Summarize();
    }

    /// <summary>
    /// Reflection discovery, post-build: announces the built corpus bindings assembly via
    /// <c>reqnroll/projectLoaded</c> (the same notification a real IDE glue component sends after a
    /// build) against a <b>fresh</b> server instance, and measures wall-clock from that notification
    /// to the moment binding discovery has completed and a corpus step resolves to a real
    /// definition.
    /// </summary>
    public static async Task<LatencySummary> ReflectionDiscoveryAsync(
        string corpusRoot, string corpusAssemblyPath, int repetitions = 3, int timeoutMs = 10_000)
    {
        var recorder = new LatencyRecorder(PerfTargets.ReflectionDiscovery.Operation);
        var featurePath = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .First();
        var featureUri = DocumentUri.FromFileSystemPath(featurePath);
        var featureText = File.ReadAllText(featurePath);
        var (line, character) = FirstBoundStepPosition(featureText);

        for (var rep = 0; rep < repetitions; rep++)
        {
            await using var harness = new BenchmarkLspHarness();
            await harness.StartAsync(corpusRoot).ConfigureAwait(false);
            harness.OpenFeature(featureUri, 1, featureText);
            // Let the initial parse settle before announcing the project, so the measured window
            // is discovery time, not a race with the first-open parse.
            await PollDefinitionAsync(harness, featureUri, line, character, 2000).ConfigureAwait(false);

            var start = Stopwatch.GetTimestamp();
            harness.SendCorpusProjectLoaded(corpusRoot, corpusAssemblyPath);

            var resolved = await PollDefinitionAsync(harness, featureUri, line, character, timeoutMs)
                .ConfigureAwait(false);
            if (resolved) recorder.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            else Console.Error.WriteLine($"  [reflection-discovery] rep {rep}: definition did not resolve within {timeoutMs}ms");
        }

        return recorder.Summarize();
    }

    private static async Task<bool> PollDefinitionAsync(
        BenchmarkLspHarness harness, DocumentUri uri, int line, int character, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var location = await harness.RequestAsync<LocationOrLocationLinks?>(
                "textDocument/definition",
                new DefinitionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position = new Position(line, character),
                }).ConfigureAwait(false);
            if (location is not null && location.Any()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }

    // The generated corpus's first scenario always starts with "Given precondition 0 is met" a few
    // lines into the file (see CorpusGenerator.BuildFeature) — the same anchor InteractiveScenarios
    // uses for its cursor positions. The offset lands partway into the step text (mirroring
    // OpenFeature.StepPosition), not at the start of the "Given " keyword, since
    // BindingMatchService.FindAt resolves a step from a position inside its text/args.
    private static (int Line, int Character) FirstBoundStepPosition(string featureText)
    {
        var lines = featureText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("Given ", StringComparison.Ordinal))
            {
                var indent = lines[i].Length - trimmed.Length;
                return (i, indent + "Given prec".Length);
            }
        }
        return (0, 0);
    }

    private static string BindingSource(string className, string pattern) => $$"""
        using Reqnroll;

        namespace Benchmark.RoslynReDiscovery;

        [Binding]
        public class {{className}}
        {
            [When(@"{{pattern}}")]
            public void When_{{className}}() { }
        }
        """;
}

/// <summary>A batch scenario that could not be measured, with the reason, for honest reporting.</summary>
public sealed record SkippedBatchScenario(PerfTarget Target, string Reason);
