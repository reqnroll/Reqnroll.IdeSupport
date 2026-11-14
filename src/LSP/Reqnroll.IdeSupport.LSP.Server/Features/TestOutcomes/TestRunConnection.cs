#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// State and message handling for one logger connection (one NDJSON stream) of a test run. The
/// socket owner feeds it the parsed <c>hello</c> line, then each following message, then tells it the
/// stream ended; it owns the counters, the running marks in the <see cref="TestOutcomeStore"/> and the
/// run-completed telemetry. It never touches a socket, so it can be driven with plain <see cref="JObject"/>s.
/// </summary>
internal sealed class TestRunConnection
{
    /// <summary>Where a connection is in its lifecycle. Only ever moves forward.</summary>
    internal enum ConnectionPhase
    {
        /// <summary>No accepted <c>hello</c> yet.</summary>
        AwaitingHello,
        /// <summary>Hello accepted, nothing stored or started: vstest.console's spare logger instance looks like this.</summary>
        Idle,
        /// <summary>A <c>runStart</c> arrived or a result was stored, and no <c>runComplete</c> yet.</summary>
        Running,
        /// <summary>A <c>runComplete</c> arrived.</summary>
        Completed,
    }

    private const string Prefix = nameof(TestOutcomeTcpListener);

    private readonly TestOutcomeStore _store;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly ILspTelemetryService? _telemetry;
    private readonly Func<int> _nextConnectionSeq;

    private string? _connectionId;
    private ConnectionPhase _phase = ConnectionPhase.AwaitingHello;
    private int _results;
    private int _passed;
    private int _failed;
    private int _skipped;
    private string _reporterKind = "unknown";
    private string _targetFramework = string.Empty;
    private Stopwatch? _stopwatch;

    public TestRunConnection(TestOutcomeStore store, IIdeSupportLogger logger, IOperationDurationRecorder recorder,
        ILspTelemetryService? telemetry, Func<int> nextConnectionSeq)
    {
        _store = store;
        _logger = logger;
        _recorder = recorder;
        _telemetry = telemetry;
        _nextConnectionSeq = nextConnectionSeq;
    }

    /// <summary>The run id the logger announced in its hello; null until <see cref="OnHello"/> accepted one.</summary>
    public string? RunId { get; private set; }

    /// <summary>The current lifecycle phase.</summary>
    public ConnectionPhase Phase => _phase;

    /// <summary>Number of results this connection stored.</summary>
    public int ResultCount => _results;

    /// <summary>
    /// Accepts the first line. Returns false (and logs) when it is not a <c>hello</c>, in which case the
    /// caller should close the connection.
    /// </summary>
    public bool OnHello(JObject hello)
    {
        if (!string.Equals(hello.Value<string>("type"), "hello", StringComparison.Ordinal))
        {
            _logger.LogWarning($"{Prefix}: first line was not a hello; closing.");
            return false;
        }

        var runId = hello.Value<string>("runId") ?? Guid.NewGuid().ToString("N");
        RunId = runId;
        // The store's running marks are owned per *connection*, not per registration id. VS Code
        // registers once per session and bakes that one id into a static runsettings file, and
        // vstest.console in design mode re-Initializes the logger several times per Run click, so
        // several live connections can share a runId — a stale, idle one dropping mid-run must not
        // clear the marks the real one set.
        _connectionId = $"{runId}/{_nextConnectionSeq()}";
        var protocol = hello.Value<int?>("protocol") ?? 0;
        _targetFramework = hello.Value<string>("targetFramework") ?? string.Empty;
        // Prefer the explicit "reporterKind" field both loggers send. Hello lines from older loggers
        // predate it: the VSTest logger always carried a (possibly empty) "targetFramework" key, the
        // MTP reporter never did.
        _reporterKind = hello.Value<string>("reporterKind")
            ?? (hello.ContainsKey("targetFramework") ? "VSTestLogger" : "MTP");
        _logger.LogInfo($"{Prefix}: run {runId} connected (protocol {protocol}, runner pid {hello.Value<string>("runnerPid")}, tfm {hello.Value<string>("targetFramework")})");
        if (protocol != 1)
            _logger.LogWarning($"{Prefix}: logger protocol {protocol} differs from expected 1; parsing best-effort.");

        // Times the ingestion hot path from hello to runComplete; the RPC handlers' own Measure()
        // calls only cover registerRun/getOutcome.
        _stopwatch = Stopwatch.StartNew();
        _phase = ConnectionPhase.Idle;
        return true;
    }

    /// <summary>Dispatches one parsed <c>runStart</c> / <c>result</c> / <c>runComplete</c> message; others are ignored.</summary>
    public void OnMessage(JObject message)
    {
        if (_phase == ConnectionPhase.AwaitingHello) return;

        switch (message.Value<string>("type"))
        {
            case "runStart": OnRunStart(message); break;
            case "result": OnResult(message); break;
            case "runComplete": OnRunComplete(message); break;
        }
    }

    /// <summary>
    /// Finalizes the connection once its stream ended, however it ended. Returns true when results were
    /// stored, i.e. the caller should persist the store and make sure the client refreshes.
    /// </summary>
    public bool OnClosed()
    {
        if (_phase == ConnectionPhase.AwaitingHello) return false;

        switch (_phase)
        {
            case ConnectionPhase.Running:
                _logger.LogWarning($"{Prefix}: run {RunId} closed without runComplete after {_results} result(s) — treated as aborted.");
                // The most interesting runs (killed test host, crashed MTP process) never send
                // runComplete; count them too so aborted runs aren't undercounted. ExecutedCount is
                // what we saw (no runComplete to read it from).
                _stopwatch?.Stop();
                SendRunCompletedTelemetry(executed: _results, aborted: true, canceled: false, completedNormally: false);
                break;
            case ConnectionPhase.Idle:
                // Never sent runStart and stored nothing: vstest.console Initializes the logger more
                // than once per run and only closes the idle sockets later — routine, not a lost run.
                _logger.LogVerbose($"{Prefix}: idle connection for run {RunId} closed without a runStart; ignoring.");
                break;
        }

        // Whether the run completed or the runner died: nothing this connection marked is running any
        // more, and whatever arrived is worth keeping for the next session.
        _store.CompleteRun(_connectionId!);
        return _results > 0;
    }

    private void OnRunStart(JObject message)
    {
        if (_phase == ConnectionPhase.Idle) _phase = ConnectionPhase.Running;
        var running = _store.MarkRunning(_connectionId!, TestOutcomeTcpListener.ParseRunStartTests(message));
        _logger.LogVerbose($"{Prefix}: run {RunId} started, {message.Value<int?>("testCount") ?? 0} test(s), {running.Count} method(s) marked running");
    }

    private void OnResult(JObject message)
    {
        var record = TestOutcomeTcpListener.ToRecord(_connectionId!, message);
        if (_store.Record(record) is null) return;

        _results++;
        if (_phase == ConnectionPhase.Idle) _phase = ConnectionPhase.Running;
        switch (record.Outcome)
        {
            case TestOutcomeKind.Passed: _passed++; break;
            case TestOutcomeKind.Failed: _failed++; break;
            case TestOutcomeKind.Skipped: _skipped++; break;
        }
    }

    private void OnRunComplete(JObject message)
    {
        _phase = ConnectionPhase.Completed;
        // Clear the running marks now rather than at socket close: the logger closes right after
        // this line, but the glyph should follow the protocol, not the FIN.
        _store.CompleteRun(_connectionId!);
        var aborted = message.Value<bool?>("aborted") ?? false;
        var canceled = message.Value<bool?>("canceled") ?? false;
        var executed = message.Value<int?>("executed") ?? 0;
        _logger.LogInfo($"{Prefix}: run {RunId} complete — executed {executed}, aborted={aborted}, canceled={canceled}, {_results} result(s) stored");

        _stopwatch?.Stop();
        _recorder.Record(CustomLspMethodNames.ReqnrollTestOutcomesIngestRun, _stopwatch?.Elapsed.TotalMilliseconds ?? 0,
            detail: $"results={_results} reporterKind={_reporterKind}");
        SendRunCompletedTelemetry(executed, aborted, canceled, completedNormally: !aborted && !canceled);
    }

    /// <summary>
    /// Product telemetry: counts, flags, a duration, the reporter kind and the target framework moniker
    /// only — no paths, no test names, no content. Run mode / scope (Run vs Debug, Scenario vs Feature)
    /// are not sent: <c>reqnroll/registerTestRun</c> takes no parameters and is called once per client
    /// session (VS Code), not per Run click, so the server never learns them.
    /// </summary>
    private void SendRunCompletedTelemetry(int executed, bool aborted, bool canceled, bool completedNormally)
    {
        var elapsedMs = _stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        _telemetry?.SendEvent(TelemetryEvents.ProjectTestRunCompleted, new Dictionary<string, object?>
        {
            ["ResultCount"] = _results,
            ["PassedCount"] = _passed,
            ["FailedCount"] = _failed,
            ["SkippedCount"] = _skipped,
            ["ExecutedCount"] = executed,
            ["Aborted"] = aborted,
            ["Canceled"] = canceled,
            ["CompletedNormally"] = completedNormally,
            ["ReporterKind"] = _reporterKind,
            ["TargetFramework"] = _targetFramework,
            ["DurationMs"] = (long)Math.Round(elapsedMs),
            ["DurationBucket"] = OperationDurationRecorder.Bucket(elapsedMs),
        });
    }
}
