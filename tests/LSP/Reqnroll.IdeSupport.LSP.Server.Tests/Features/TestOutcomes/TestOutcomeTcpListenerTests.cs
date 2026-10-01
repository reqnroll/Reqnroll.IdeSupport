using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// <see cref="TestOutcomeTcpListener"/> over a real loopback socket, driven by hand-written NDJSON in
/// the exact shape <c>Reqnroll.IdeSupport.TestLogger</c> emits. The logger's own serializer is covered
/// in its own test project; this is the receiving contract. Moved here from the Visual Studio-only
/// VSSDKIntegration project's <c>TestOutcomeListenerTests</c> (LSP-server outcome pipeline refactor).
/// </summary>
public class TestOutcomeTcpListenerTests : IDisposable
{
    private readonly TestOutcomeStore _store = new();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ManualResetEventSlim _refreshed = new();
    private int _refreshCount;
    private readonly TestOutcomeTcpListener _listener;

    public TestOutcomeTcpListenerTests()
    {
        _listener = new TestOutcomeTcpListener(_store, _logger, () => { Interlocked.Increment(ref _refreshCount); _refreshed.Set(); });
    }

    public void Dispose() => _listener.Dispose();

    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";

    private static string Hello(string runId = "run-1", int protocol = 1)
        => $"{{\"type\":\"hello\",\"protocol\":{protocol},\"runId\":\"{runId}\",\"runnerPid\":4242,\"idePid\":\"1\",\"targetFramework\":\".NETCoreApp,Version=v8.0\"}}";

    private static string Result(string method, string display, string outcome, string runId = "run-1", string? stdout = null)
        => new JObject
        {
            ["type"] = "result",
            ["runId"] = runId,
            ["source"] = Source,
            ["managedType"] = "Specs.CalcFeature",
            ["managedMethod"] = method + "(System.String)",
            ["fqn"] = "Specs.CalcFeature." + method,
            ["displayName"] = display,
            ["outcome"] = outcome,
            ["durationMs"] = 3.5,
            ["errorMessage"] = outcome == "Failed" ? "boom" : null,
            ["errorStackTrace"] = null,
            ["stdout"] = stdout,
            ["stdoutTruncated"] = false,
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string RunComplete(string runId = "run-1")
        => $"{{\"type\":\"runComplete\",\"runId\":\"{runId}\",\"executed\":2,\"aborted\":false,\"canceled\":false,\"elapsedMs\":10}}";

    private static async Task SendAsync(string endpoint, params string[] lines)
    {
        var colon = endpoint.LastIndexOf(':');
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Substring(0, colon), int.Parse(endpoint.Substring(colon + 1)));
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length);
        await stream.FlushAsync();
        // Give the listener a moment to drain before the socket closes under it.
        await Task.Delay(50);
    }

    private async Task<bool> WaitForStoreAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public void RegisterRun_starts_a_loopback_listener_and_mints_distinct_run_ids()
    {
        _listener.Endpoint.Should().BeNull();

        var first = _listener.RegisterRun()!;
        var second = _listener.RegisterRun()!;

        first.Endpoint.Should().StartWith("127.0.0.1:").And.Be(second.Endpoint, "one listener per server instance");
        first.RunId.Should().NotBe(second.RunId);
    }

    [Fact]
    public async Task A_run_lands_in_the_store_and_notifies_the_client()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.RunId),
            Result("Add", "Add(1,2)", "Passed", registration.RunId),
            Result("Add", "Add(3,4)", "Failed", registration.RunId),
            RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 2)).Should().BeTrue();
        var outcome = _store.TryGet(Source, "Specs.CalcFeature", "Add")!;
        outcome.Aggregate.Should().Be(TestOutcomeKind.Failed);
        outcome.Rows.Should().Contain(r => r.DisplayName == "Add(3,4)" && r.ErrorMessage == "boom");
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the debounced change notification must fire after results arrive");
    }

    [Fact]
    public async Task Results_arriving_in_a_burst_coalesce_into_one_change_notification()
    {
        var registration = _listener.RegisterRun()!;

        var lines = new List<string> { Hello(registration.RunId) };
        for (var i = 0; i < 25; i++) lines.Add(Result("Add", $"row {i}", "Passed", registration.RunId));
        lines.Add(RunComplete(registration.RunId));
        await SendAsync(registration.Endpoint, lines.ToArray());

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 25)).Should().BeTrue();
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        await Task.Delay(TestOutcomeTcpListener.RefreshDebounce + TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref _refreshCount).Should().BeLessThanOrEqualTo(2, "25 results within the debounce window are one notification, plus at most one trailing notification on close");
    }

    [Fact]
    public async Task A_connection_that_never_registered_a_run_is_still_accepted()
    {
        // There is no per-connection secret any more (see TestOutcomeTcpListener's remarks) — the
        // loopback bind is the whole trust boundary, so a connection naming a run id nobody asked for
        // is processed exactly like any other.
        await SendAsync(_listener.RegisterRun()!.Endpoint,
            Hello("uninvited"),
            Result("Add", "Add(1,2)", "Passed", "uninvited"),
            RunComplete("uninvited"));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
    }

    [Fact]
    public async Task A_registration_is_reusable_across_multiple_connections()
    {
        // The bug this fixes: VS Code mints one registration per extension activation (there is no
        // "a run is about to start" hook to rotate anything against), so it can see several test-host
        // connections against the same registration in one session. A single-use secret rejected every
        // connection after the first; without one, every connection for the same run id is accepted.
        var registration = _listener.RegisterRun()!;
        await SendAsync(registration.Endpoint, Hello(registration.RunId), Result("Add", "first", "Passed", registration.RunId), RunComplete(registration.RunId));
        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();

        await SendAsync(registration.Endpoint, Hello(registration.RunId), Result("Add", "second", "Passed", registration.RunId), RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 2)).Should().BeTrue();
        _store.TryGet(Source, "Specs.CalcFeature", "Add")!.Rows.Should().Contain(r => r.DisplayName == "first")
            .And.Contain(r => r.DisplayName == "second");
    }

    [Fact]
    public async Task A_first_line_that_is_not_a_hello_is_rejected()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint, Result("Add", "Add(1,2)", "Passed", registration.RunId));

        await Task.Delay(300);
        _store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Results_before_an_abrupt_close_are_kept()
    {
        var registration = _listener.RegisterRun()!;

        // No runComplete — the runner died, or the user cancelled.
        await SendAsync(registration.Endpoint, Hello(registration.RunId), Result("Add", "Add(1,2)", "Passed", registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public async Task Garbage_lines_are_skipped_and_later_results_still_land()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.RunId),
            "this is not json",
            "",
            Result("Add", "Add(1,2)", "Passed", registration.RunId),
            RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
    }

    private static string RunStart(string runId, params string[] identities)
        => new JObject
        {
            ["type"] = "runStart",
            ["runId"] = runId,
            ["testCount"] = identities.Length,
            ["sources"] = new JArray(Source),
            ["tests"] = new JArray(identities),
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string Identity(string method, string display)
        => string.Join(TestOutcomeTcpListener.TestIdentitySeparator.ToString(), Source, "Specs.CalcFeature", method + "(System.String)", "Specs.CalcFeature." + method, display);

    [Fact]
    public async Task RunStart_marks_the_listed_methods_running_and_runComplete_clears_them()
    {
        var registration = _listener.RegisterRun()!;
        var key = TestOutcomeKey.ForLookup(Source, "Specs.CalcFeature", "Add");

        // Keeps the connection open (unlike SendAsync's helper, which sends then closes after a fixed
        // delay) so "still running" is asserted while the socket is provably still open, rather than
        // racing the server's EOF detection against the assertion — on net10.0's socket stack a
        // loopback FIN is detected fast enough that SendAsync's old fixed 50ms grace window had
        // already closed and cleaned up the run before the assertion ran (net481, where this test
        // originated, tolerated that timing; net10.0 does not).
        var colon = registration.Endpoint.LastIndexOf(':');
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(registration.Endpoint.Substring(0, colon), int.Parse(registration.Endpoint.Substring(colon + 1)));
            using var stream = client.GetStream();
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n",
                Hello(registration.RunId),
                RunStart(registration.RunId, Identity("Add", "Add(1,2)"), Identity("Add", "Add(3,4)"), Identity("Sub", "Sub"))) + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();

            (await WaitForStoreAsync(() => _store.TryGet(key)?.IsRunning == true)).Should().BeTrue();
            _store.TryGet(Source, "Specs.CalcFeature", "Sub")!.IsRunning.Should().BeTrue();
        } // Connection closes here, without a runComplete.

        // The connection above closed without runComplete → the listener completes the run itself.
        (await WaitForStoreAsync(() => _store.TryGet(key) is null)).Should().BeTrue("a method that only ever ran, never reported, is forgotten");
        _store.TryGet(Source, "Specs.CalcFeature", "Sub").Should().BeNull();
    }

    private static async Task<TcpClient> OpenAsync(string endpoint, params string[] lines)
    {
        var colon = endpoint.LastIndexOf(':');
        var client = new TcpClient();
        await client.ConnectAsync(endpoint.Substring(0, colon), int.Parse(endpoint.Substring(colon + 1)));
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
        await client.GetStream().FlushAsync();
        return client;
    }

    /// <summary>
    /// Live VS Code log, 2026-09-20: the extension registers one runId per session and vstest.console
    /// (design mode) opens 1–2 extra logger connections per Run click that say hello but never send
    /// runStart, dropping them only later — sometimes while the real connection's run is still going.
    /// Those idle drops must not clear the real run's running marks, and must not be logged as aborts.
    /// </summary>
    [Fact]
    public async Task An_idle_connection_sharing_the_runId_does_not_clear_another_connections_running_marks()
    {
        var registration = _listener.RegisterRun()!;
        var add = TestOutcomeKey.ForLookup(Source, "Specs.CalcFeature", "Add");

        using var idle = await OpenAsync(registration.Endpoint, Hello(registration.RunId));
        using var real = await OpenAsync(registration.Endpoint,
            Hello(registration.RunId),
            RunStart(registration.RunId, Identity("Add", "Add(1,2)"), Identity("Add", "Add(3,4)")));
        (await WaitForStoreAsync(() => _store.TryGet(add)?.IsRunning == true)).Should().BeTrue();

        // The spare logger instance goes away mid-run.
        idle.Close();
        await Task.Delay(300);
        _store.TryGet(add)!.IsRunning.Should().BeTrue("only the connection that set a running mark may clear it");
        // LogWarning is an extension over IIdeSupportLogger.Log(LogMessage); assert on the underlying call.
        _logger.DidNotReceive().Log(Arg.Is<LogMessage>(m => m.Level == System.Diagnostics.TraceLevel.Warning && m.Message.Contains("closed without runComplete")));
        _logger.Received().Log(Arg.Is<LogMessage>(m => m.Message.Contains("idle connection") && m.Message.Contains("ignoring")));

        // The first row lands on the real connection: still running, the run isn't over yet.
        var bytes = Encoding.UTF8.GetBytes(Result("Add", "Add(1,2)", "Passed", registration.RunId) + "\n");
        await real.GetStream().WriteAsync(bytes, 0, bytes.Length);
        await real.GetStream().FlushAsync();
        (await WaitForStoreAsync(() => _store.TryGet(add)?.Rows.Count == 1)).Should().BeTrue();
        _store.TryGet(add)!.IsRunning.Should().BeTrue("a result from the same connection keeps the method running until that connection completes");

        bytes = Encoding.UTF8.GetBytes(RunComplete(registration.RunId) + "\n");
        await real.GetStream().WriteAsync(bytes, 0, bytes.Length);
        await real.GetStream().FlushAsync();
        (await WaitForStoreAsync(() => _store.TryGet(add)?.IsRunning == false)).Should().BeTrue("runComplete ends the running state without waiting for the socket to close");
        _store.TryGet(add)!.Aggregate.Should().Be(TestOutcomeKind.Passed);
        real.Close();
        await Task.Delay(200);
        _store.TryGet(add)!.Rows.Should().ContainSingle("closing the completed connection keeps its results");
    }

    [Fact]
    public async Task A_full_run_persists_its_outcomes_on_completion()
    {
        var file = Path.Combine(Path.GetTempPath(), "reqnroll-listener-persist-tests", Guid.NewGuid().ToString("N"), "test-outcomes.json");
        var persistence = new TestOutcomePersistence(file, _ => DateTime.MinValue, _logger);
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, persistence);
        var registration = listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.RunId),
            RunStart(registration.RunId, Identity("Add", "Add(1,2)")),
            Result("Add", "Add(1,2)", "Passed", registration.RunId),
            RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => File.Exists(file))).Should().BeTrue();
        persistence.Load().Should().ContainSingle().Which.Key.MethodName.Should().Be("Add");
        _store.TryGet(Source, "Specs.CalcFeature", "Add")!.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void ParseRunStartTests_unpacks_identities_dedupes_by_method_and_skips_malformed_entries()
    {
        var message = JObject.Parse(RunStart("r", Identity("Add", "Add(1,2)"), Identity("Add", "Add(3,4)"), "garbage", "", Identity("Sub", "Sub")));

        var keys = TestOutcomeTcpListener.ParseRunStartTests(message);

        keys.Select(k => k.MethodName).Should().Equal("Add", "Sub");
        keys[0].TypeFullName.Should().Be("Specs.CalcFeature");
    }

    [Fact]
    public void ParseRunStartTests_is_empty_for_a_source_based_run()
        => TestOutcomeTcpListener.ParseRunStartTests(JObject.Parse("{\"type\":\"runStart\",\"runId\":\"r\",\"testCount\":0,\"sources\":[\"x.dll\"]}")).Should().BeEmpty();

    [Fact]
    public void ToRecord_maps_every_wire_field_and_defaults_the_missing_ones()
    {
        var message = JObject.Parse(Result("Add", "Add(1,2)", "Failed", stdout: "Given x\n-> done: ..."));

        var record = TestOutcomeTcpListener.ToRecord("run-1/7", message);

        record.RunId.Should().Be("run-1/7", "the connection id, not the wire runId, is what the store matches running marks against");
        record.Source.Should().Be(Source);
        record.ManagedType.Should().Be("Specs.CalcFeature");
        record.ManagedMethod.Should().Be("Add(System.String)");
        record.FullyQualifiedName.Should().Be("Specs.CalcFeature.Add");
        record.DisplayName.Should().Be("Add(1,2)");
        record.Outcome.Should().Be(TestOutcomeKind.Failed);
        record.DurationMs.Should().Be(3.5);
        record.ErrorMessage.Should().Be("boom");
        record.Stdout.Should().Be("Given x\n-> done: ...");
        record.StdoutTruncated.Should().BeFalse();

        var sparse = TestOutcomeTcpListener.ToRecord("run-1/8", JObject.Parse("{\"type\":\"result\",\"source\":\"x.dll\",\"fqn\":\"A.B\"}"));
        sparse.RunId.Should().Be("run-1/8");
        sparse.DisplayName.Should().Be("A.B");
        sparse.Outcome.Should().Be(TestOutcomeKind.None);
    }

    [Fact]
    public void Dispose_stops_the_listener_and_further_registrations_return_null()
    {
        var registration = _listener.RegisterRun()!;
        _listener.Dispose();

        _listener.RegisterRun().Should().BeNull();
        FluentActions.Invoking(() =>
        {
            var colon = registration.Endpoint.LastIndexOf(':');
            using var client = new TcpClient();
            client.Connect(registration.Endpoint.Substring(0, colon), int.Parse(registration.Endpoint.Substring(colon + 1)));
        }).Should().Throw<SocketException>();
    }

    // ── Issue #722: telemetry + perf instrumentation ────────────────────────────────────────

    [Fact]
    public async Task RunComplete_sends_the_TestOutcomesRunCompleted_telemetry_event_with_counts_and_VSTestLogger_reporterKind()
    {
        var lspTelemetry = Substitute.For<ILspTelemetryService>();
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, lspTelemetryService: lspTelemetry);
        var registration = listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.RunId), // Hello() includes targetFramework, matching the real VSTest logger's hello.
            Result("Add", "Add(1,2)", "Passed", registration.RunId),
            Result("Add", "Add(3,4)", "Failed", registration.RunId),
            RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 2)).Should().BeTrue();
        lspTelemetry.Received(1).SendEvent(TelemetryEvents.TestOutcomesRunCompleted, Arg.Is<Dictionary<string, object?>>(p =>
            (int)p["ResultCount"]! == 2 &&
            (bool)p["Aborted"]! == false &&
            (bool)p["Canceled"]! == false &&
            (string)p["ReporterKind"]! == "VSTestLogger"));
    }

    [Fact]
    public async Task RunComplete_infers_MTP_reporterKind_when_hello_has_no_targetFramework_field()
    {
        var lspTelemetry = Substitute.For<ILspTelemetryService>();
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, lspTelemetryService: lspTelemetry);
        var registration = listener.RegisterRun()!;

        // Mirrors ReqnrollMtpReporter's hello line: no targetFramework/idePid keys at all.
        var mtpHello = new JObject { ["type"] = "hello", ["protocol"] = 1, ["runId"] = registration.RunId, ["runnerPid"] = 4242, ["connected"] = true }
            .ToString(Newtonsoft.Json.Formatting.None);

        await SendAsync(registration.Endpoint, mtpHello, Result("Add", "Add(1,2)", "Passed", registration.RunId), RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
        lspTelemetry.Received(1).SendEvent(TelemetryEvents.TestOutcomesRunCompleted, Arg.Is<Dictionary<string, object?>>(p => (string)p["ReporterKind"]! == "MTP"));
    }

    [Fact]
    public async Task RunComplete_prefers_explicit_reporterKind_over_targetFramework_inference()
    {
        // The new MTP reporter hello carries BOTH targetFramework and an explicit reporterKind="MTP".
        // The explicit field must win — presence of targetFramework alone must not mislabel it VSTestLogger.
        var lspTelemetry = Substitute.For<ILspTelemetryService>();
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, lspTelemetryService: lspTelemetry);
        var registration = listener.RegisterRun()!;

        var mtpHello = new JObject
        {
            ["type"] = "hello",
            ["protocol"] = 1,
            ["runId"] = registration.RunId,
            ["runnerPid"] = 4242,
            ["connected"] = true,
            ["reporterKind"] = "MTP",
            ["targetFramework"] = ".NETCoreApp,Version=v10.0",
        }.ToString(Newtonsoft.Json.Formatting.None);

        await SendAsync(registration.Endpoint, mtpHello, Result("Add", "Add(1,2)", "Passed", registration.RunId), RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
        lspTelemetry.Received(1).SendEvent(TelemetryEvents.TestOutcomesRunCompleted, Arg.Is<Dictionary<string, object?>>(p => (string)p["ReporterKind"]! == "MTP"));
    }

    [Fact]
    public async Task RunComplete_records_the_ingestion_duration_via_the_operation_recorder()
    {
        var recorder = Substitute.For<IOperationDurationRecorder>();
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, recorder: recorder);
        var registration = listener.RegisterRun()!;

        await SendAsync(registration.Endpoint, Hello(registration.RunId), Result("Add", "Add(1,2)", "Passed", registration.RunId), RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
        recorder.Received(1).Record(CustomLspMethodNames.ReqnrollTestOutcomesIngestRun, Arg.Any<double>(), Arg.Any<OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task An_unparseable_hello_line_is_forwarded_to_telemetry_when_a_telemetry_service_is_supplied()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        using var listener = new TestOutcomeTcpListener(_store, _logger, () => { }, telemetryService: telemetry);
        var registration = listener.RegisterRun()!;

        // Not JSON at all (unlike "A_first_line_that_is_not_a_hello_is_rejected", which sends a
        // well-formed "result" line): JObject.Parse throws synchronously outside the read loop's own
        // per-line try/catch, exercising the handler's general catch-all → LogException(telemetryService, ...).
        await SendAsync(registration.Endpoint, "this is not json at all {{{");

        await Task.Delay(300);
        telemetry.Received(1).MonitorError(Arg.Any<Exception>());
    }
}
