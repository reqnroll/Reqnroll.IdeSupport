using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// <see cref="TestOutcomeListener"/> over a real loopback socket, driven by hand-written NDJSON in the
/// exact shape <c>Reqnroll.IdeSupport.TestLogger</c> emits. The logger's own serializer is covered in its
/// own test project; this is the receiving contract.
/// </summary>
public class TestOutcomeListenerTests : IDisposable
{
    private readonly TestOutcomeStore _store = new();
    private readonly ManualResetEventSlim _refreshed = new();
    private int _refreshCount;
    private readonly TestOutcomeListener _listener;

    public TestOutcomeListenerTests()
    {
        _listener = new TestOutcomeListener(_store, () => { Interlocked.Increment(ref _refreshCount); _refreshed.Set(); });
    }

    public void Dispose() => _listener.Dispose();

    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";

    private static string Hello(string token, string runId = "run-1", int protocol = 1)
        => $"{{\"type\":\"hello\",\"protocol\":{protocol},\"token\":\"{token}\",\"runId\":\"{runId}\",\"runnerPid\":4242,\"idePid\":\"1\",\"targetFramework\":\".NETCoreApp,Version=v8.0\"}}";

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
    public void RegisterRun_starts_a_loopback_listener_and_mints_distinct_tokens()
    {
        _listener.Endpoint.Should().BeNull();

        var first = _listener.RegisterRun()!;
        var second = _listener.RegisterRun()!;

        first.Endpoint.Should().StartWith("127.0.0.1:").And.Be(second.Endpoint, "one listener per VS instance");
        first.Token.Should().NotBe(second.Token);
        first.RunId.Should().NotBe(second.RunId);
        first.Token.Should().MatchRegex("^[A-Za-z0-9_-]{20,}$", "token must be safe to embed in runsettings XML and a command line");
    }

    [Fact]
    public async Task A_run_with_a_valid_token_lands_in_the_store_and_refreshes_the_lenses()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.Token, registration.RunId),
            Result("Add", "Add(1,2)", "Passed", registration.RunId),
            Result("Add", "Add(3,4)", "Failed", registration.RunId),
            RunComplete(registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 2)).Should().BeTrue();
        var outcome = _store.TryGet(Source, "Specs.CalcFeature", "Add")!;
        outcome.Aggregate.Should().Be(TestOutcomeKind.Failed);
        outcome.Rows.Should().Contain(r => r.DisplayName == "Add(3,4)" && r.ErrorMessage == "boom");
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the debounced lens refresh must fire after results arrive");
    }

    [Fact]
    public async Task Results_arriving_in_a_burst_coalesce_into_one_lens_refresh()
    {
        var registration = _listener.RegisterRun()!;

        var lines = new System.Collections.Generic.List<string> { Hello(registration.Token, registration.RunId) };
        for (var i = 0; i < 25; i++) lines.Add(Result("Add", $"row {i}", "Passed", registration.RunId));
        lines.Add(RunComplete(registration.RunId));
        await SendAsync(registration.Endpoint, lines.ToArray());

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add")?.Rows.Count == 25)).Should().BeTrue();
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        await Task.Delay(TestOutcomeListener.RefreshDebounce + TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref _refreshCount).Should().BeLessThanOrEqualTo(2, "25 results within the debounce window are one refresh, plus at most one trailing refresh on close");
    }

    [Fact]
    public async Task A_connection_with_an_unknown_token_is_dropped_without_touching_the_store()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello("not-a-token", "rogue"),
            Result("Add", "Add(1,2)", "Passed", "rogue"),
            RunComplete("rogue"));

        await Task.Delay(300);
        _store.Snapshot().Should().BeEmpty();
        Volatile.Read(ref _refreshCount).Should().Be(0);
    }

    [Fact]
    public async Task A_token_is_single_use()
    {
        var registration = _listener.RegisterRun()!;
        await SendAsync(registration.Endpoint, Hello(registration.Token, registration.RunId), Result("Add", "first", "Passed", registration.RunId), RunComplete(registration.RunId));
        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();

        await SendAsync(registration.Endpoint, Hello(registration.Token, registration.RunId), Result("Add", "second", "Passed", registration.RunId), RunComplete(registration.RunId));

        await Task.Delay(300);
        _store.TryGet(Source, "Specs.CalcFeature", "Add")!.Rows.Should().ContainSingle(r => r.DisplayName == "first");
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
        await SendAsync(registration.Endpoint, Hello(registration.Token, registration.RunId), Result("Add", "Add(1,2)", "Passed", registration.RunId));

        (await WaitForStoreAsync(() => _store.TryGet(Source, "Specs.CalcFeature", "Add") is not null)).Should().BeTrue();
        _refreshed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public async Task Garbage_lines_are_skipped_and_later_results_still_land()
    {
        var registration = _listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.Token, registration.RunId),
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
        => string.Join(TestOutcomeListener.TestIdentitySeparator.ToString(), Source, "Specs.CalcFeature", method + "(System.String)", "Specs.CalcFeature." + method, display);

    [Fact]
    public async Task RunStart_marks_the_listed_methods_running_and_runComplete_clears_them()
    {
        var registration = _listener.RegisterRun()!;
        var key = TestOutcomeKey.ForLookup(Source, "Specs.CalcFeature", "Add");

        await SendAsync(registration.Endpoint,
            Hello(registration.Token, registration.RunId),
            RunStart(registration.RunId, Identity("Add", "Add(1,2)"), Identity("Add", "Add(3,4)"), Identity("Sub", "Sub")));

        (await WaitForStoreAsync(() => _store.TryGet(key)?.IsRunning == true)).Should().BeTrue();
        _store.TryGet(Source, "Specs.CalcFeature", "Sub")!.IsRunning.Should().BeTrue();

        // The connection above closed without runComplete → the listener completes the run itself.
        (await WaitForStoreAsync(() => _store.TryGet(key) is null)).Should().BeTrue("a method that only ever ran, never reported, is forgotten");
        _store.TryGet(Source, "Specs.CalcFeature", "Sub").Should().BeNull();
    }

    [Fact]
    public async Task A_full_run_persists_its_outcomes_on_completion()
    {
        var file = Path.Combine(Path.GetTempPath(), "reqnroll-listener-persist-tests", Guid.NewGuid().ToString("N"), "test-outcomes.json");
        var persistence = new TestOutcomePersistence(file, _ => DateTime.MinValue);
        using var listener = new TestOutcomeListener(_store, () => { }, persistence);
        var registration = listener.RegisterRun()!;

        await SendAsync(registration.Endpoint,
            Hello(registration.Token, registration.RunId),
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

        var keys = TestOutcomeListener.ParseRunStartTests(message);

        keys.Select(k => k.MethodName).Should().Equal("Add", "Sub");
        keys[0].TypeFullName.Should().Be("Specs.CalcFeature");
    }

    [Fact]
    public void ParseRunStartTests_is_empty_for_a_source_based_run()
        => TestOutcomeListener.ParseRunStartTests(JObject.Parse("{\"type\":\"runStart\",\"runId\":\"r\",\"testCount\":0,\"sources\":[\"x.dll\"]}")).Should().BeEmpty();

    [Fact]
    public void ToRecord_maps_every_wire_field_and_defaults_the_missing_ones()
    {
        var message = JObject.Parse(Result("Add", "Add(1,2)", "Failed", stdout: "Given x\n-> done: ..."));

        var record = TestOutcomeListener.ToRecord("fallback-run", message);

        record.RunId.Should().Be("run-1");
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

        var sparse = TestOutcomeListener.ToRecord("fallback-run", JObject.Parse("{\"type\":\"result\",\"source\":\"x.dll\",\"fqn\":\"A.B\"}"));
        sparse.RunId.Should().Be("fallback-run");
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
}
