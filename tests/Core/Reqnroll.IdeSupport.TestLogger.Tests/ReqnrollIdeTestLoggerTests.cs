using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Reqnroll.IdeSupport.TestLogger;
using Xunit;

namespace Reqnroll.IdeSupport.TestLogger.Tests;

/// <summary>
/// The logger hosted in-process against a hand-rolled <see cref="TestLoggerEvents"/>, with a real
/// loopback listener standing in for the IDE. Parsing is done with System.Text.Json — a different
/// library from both the logger's hand-rolled writer and the VS side's Newtonsoft reader — so the wire
/// format is checked against a neutral third party.
/// </summary>
public class ReqnrollIdeTestLoggerTests
{
    /// <summary>Raises the abstract events vstest's runner would raise.</summary>
    private sealed class FakeEvents : TestLoggerEvents
    {
        public override event EventHandler<TestRunMessageEventArgs>? TestRunMessage;
        public override event EventHandler<TestRunStartEventArgs>? TestRunStart;
        public override event EventHandler<TestResultEventArgs>? TestResult;
        public override event EventHandler<TestRunCompleteEventArgs>? TestRunComplete;
        public override event EventHandler<DiscoveryStartEventArgs>? DiscoveryStart;
        public override event EventHandler<TestRunMessageEventArgs>? DiscoveryMessage;
        public override event EventHandler<DiscoveredTestsEventArgs>? DiscoveredTests;
        public override event EventHandler<DiscoveryCompleteEventArgs>? DiscoveryComplete;

        public void RaiseRunStart(params TestCase[] tests) => TestRunStart?.Invoke(this, new TestRunStartEventArgs(new TestRunCriteria(tests, 10)));
        public void RaiseResult(TestResult result) => TestResult?.Invoke(this, new TestResultEventArgs(result));
        public void RaiseComplete(long executed, bool aborted = false, bool canceled = false)
            => TestRunComplete?.Invoke(this, new TestRunCompleteEventArgs(new TestRunStatistics(executed, new Dictionary<TestOutcome, long>()), canceled, aborted, null, new Collection<AttachmentSet>(), TimeSpan.FromMilliseconds(250)));

        // Silence "never used" for the discovery events we never raise.
        public void Touch() { _ = TestRunMessage; _ = DiscoveryStart; _ = DiscoveryMessage; _ = DiscoveredTests; _ = DiscoveryComplete; }
    }

    private static readonly TestProperty ManagedTypeProperty = TestProperty.Register("TestCase.ManagedType", "ManagedType", typeof(string), typeof(TestCase));
    private static readonly TestProperty ManagedMethodProperty = TestProperty.Register("TestCase.ManagedMethod", "ManagedMethod", typeof(string), typeof(TestCase));

    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";

    private static TestCase Case(string method, string display, string managedMethod)
    {
        var tc = new TestCase($"Specs.CalcFeature.{method}", new Uri("executor://fixture/v1"), Source) { DisplayName = display };
        tc.SetPropertyValue(ManagedTypeProperty, "Specs.CalcFeature");
        tc.SetPropertyValue(ManagedMethodProperty, managedMethod);
        return tc;
    }

    private static TestResult Result(TestCase tc, TestOutcome outcome, string? stdout = null, string? error = null)
    {
        var result = new TestResult(tc) { Outcome = outcome, Duration = TimeSpan.FromMilliseconds(42), DisplayName = tc.DisplayName, ErrorMessage = error };
        if (stdout is not null) result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, stdout));
        result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, "stderr noise must not be forwarded as stdout"));
        return result;
    }

    /// <summary>Accepts one connection and collects every line until the peer closes.</summary>
    private sealed class FakeIde : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly Task<List<string>> Lines;

        public FakeIde()
        {
            _listener.Start();
            Lines = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
                var lines = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null) lines.Add(line);
                return lines;
            });
        }

        public string Endpoint => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        public void Dispose() => _listener.Stop();
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public async Task Streams_hello_runStart_results_and_runComplete_to_the_endpoint()
    {
        using var ide = new FakeIde();
        var events = new FakeEvents();
        var logger = new ReqnrollIdeTestLogger();
        logger.Initialize(events, new Dictionary<string, string?>
        {
            [ReqnrollIdeTestLogger.EndpointParameter] = ide.Endpoint,
            [ReqnrollIdeTestLogger.TokenParameter] = "tok-123",
            [ReqnrollIdeTestLogger.RunIdParameter] = "run-1",
            [ReqnrollIdeTestLogger.IdeProcessIdParameter] = "999",
            [DefaultLoggerParameterNames.TargetFramework] = ".NETCoreApp,Version=v8.0",
        });

        var row1 = Case("So23", "so23(Electric guitar,1,180.0,2)", "So23(System.String,System.String[])");
        var row2 = Case("So23", "so23(Guitar pick,10,15.0,3)", "So23(System.String,System.String[])");
        events.RaiseRunStart(row1, row2);
        events.RaiseResult(Result(row1, TestOutcome.Passed, stdout: "Given the first number is 1\n-> done: Steps.Given(1) (0.0s)\n"));
        events.RaiseResult(Result(row2, TestOutcome.Failed, error: "Assert.AreEqual failed"));
        events.RaiseComplete(2);

        var lines = await ide.Lines.WaitAsync(TimeSpan.FromSeconds(10));
        lines.Should().HaveCount(5);

        var hello = Parse(lines[0]);
        hello.GetProperty("type").GetString().Should().Be("hello");
        hello.GetProperty("protocol").GetInt32().Should().Be(ReqnrollIdeTestLogger.ProtocolVersion);
        hello.GetProperty("token").GetString().Should().Be("tok-123");
        hello.GetProperty("runId").GetString().Should().Be("run-1");
        hello.GetProperty("idePid").GetString().Should().Be("999");
        hello.GetProperty("runnerPid").GetInt32().Should().Be(Environment.ProcessId);
        hello.GetProperty("targetFramework").GetString().Should().Be(".NETCoreApp,Version=v8.0");

        var start = Parse(lines[1]);
        start.GetProperty("type").GetString().Should().Be("runStart");
        start.GetProperty("testCount").GetInt32().Should().Be(2);
        start.GetProperty("sources").EnumerateArray().Select(e => e.GetString()).Should().Equal(Source);
        var identities = start.GetProperty("tests").EnumerateArray().Select(e => e.GetString()!.Split(ReqnrollIdeTestLogger.TestIdentitySeparator)).ToList();
        identities.Should().HaveCount(2);
        identities[0].Should().Equal(Source, "Specs.CalcFeature", "So23(System.String,System.String[])", "Specs.CalcFeature.So23", "so23(Electric guitar,1,180.0,2)");
        identities[1][4].Should().Be("so23(Guitar pick,10,15.0,3)");

        var r1 = Parse(lines[2]);
        r1.GetProperty("type").GetString().Should().Be("result");
        r1.GetProperty("runId").GetString().Should().Be("run-1");
        r1.GetProperty("source").GetString().Should().Be(Source);
        r1.GetProperty("managedType").GetString().Should().Be("Specs.CalcFeature");
        r1.GetProperty("managedMethod").GetString().Should().Be("So23(System.String,System.String[])");
        r1.GetProperty("fqn").GetString().Should().Be("Specs.CalcFeature.So23");
        r1.GetProperty("displayName").GetString().Should().Be("so23(Electric guitar,1,180.0,2)");
        r1.GetProperty("outcome").GetString().Should().Be("Passed");
        r1.GetProperty("durationMs").GetDouble().Should().Be(42);
        r1.GetProperty("errorMessage").ValueKind.Should().Be(JsonValueKind.Null);
        r1.GetProperty("stdout").GetString().Should().Be("Given the first number is 1\n-> done: Steps.Given(1) (0.0s)\n");
        r1.GetProperty("stdoutTruncated").GetBoolean().Should().BeFalse();

        var r2 = Parse(lines[3]);
        r2.GetProperty("outcome").GetString().Should().Be("Failed");
        r2.GetProperty("errorMessage").GetString().Should().Be("Assert.AreEqual failed");
        r2.GetProperty("stdout").ValueKind.Should().Be(JsonValueKind.Null, "stderr is not stdout and there was no stdout");

        var complete = Parse(lines[4]);
        complete.GetProperty("type").GetString().Should().Be("runComplete");
        complete.GetProperty("executed").GetInt64().Should().Be(2);
        complete.GetProperty("aborted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Without_an_endpoint_or_file_the_logger_is_inert()
    {
        var events = new FakeEvents();
        var logger = new ReqnrollIdeTestLogger();

        logger.Initialize(events, new Dictionary<string, string?>());
        // Raising events must not throw even though nothing is subscribed.
        events.RaiseRunStart();
        events.RaiseResult(Result(Case("A", "A", "A()"), TestOutcome.Passed));
        events.RaiseComplete(1);
        events.Touch();
    }

    [Fact]
    public async Task An_unreachable_endpoint_does_not_throw_and_falls_back_to_the_file_mirror()
    {
        var file = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-tests", Guid.NewGuid().ToString("N"), "mirror.ndjson");
        var events = new FakeEvents();
        var logger = new ReqnrollIdeTestLogger();

        // A port nothing listens on: grab one, then release it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        logger.Initialize(events, new Dictionary<string, string?>
        {
            [ReqnrollIdeTestLogger.EndpointParameter] = $"127.0.0.1:{port}",
            [ReqnrollIdeTestLogger.LogFilePathParameter] = file,
        });
        events.RaiseResult(Result(Case("A", "A", "A()"), TestOutcome.Passed));
        events.RaiseComplete(1);

        await Task.Delay(50);
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(3);
        Parse(lines[0]).GetProperty("connected").GetBoolean().Should().BeFalse();
        Parse(lines[1]).GetProperty("type").GetString().Should().Be("result");
    }

    [Fact]
    public async Task Stdout_is_capped_and_flagged()
    {
        using var ide = new FakeIde();
        var events = new FakeEvents();
        new ReqnrollIdeTestLogger().Initialize(events, new Dictionary<string, string?> { [ReqnrollIdeTestLogger.EndpointParameter] = ide.Endpoint });

        events.RaiseResult(Result(Case("A", "A", "A()"), TestOutcome.Passed, stdout: new string('x', ReqnrollIdeTestLogger.MaxStdoutLength + 10)));
        events.RaiseComplete(1);

        var lines = await ide.Lines.WaitAsync(TimeSpan.FromSeconds(10));
        var result = Parse(lines[1]);
        result.GetProperty("stdout").GetString()!.Length.Should().Be(ReqnrollIdeTestLogger.MaxStdoutLength);
        result.GetProperty("stdoutTruncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Cases_without_managed_identity_still_ship_their_fqn()
    {
        using var ide = new FakeIde();
        var events = new FakeEvents();
        new ReqnrollIdeTestLogger().Initialize(events, new Dictionary<string, string?> { [ReqnrollIdeTestLogger.EndpointParameter] = ide.Endpoint });

        var tc = new TestCase("Ns.Cls.Method(\"a\")", new Uri("executor://nunit/v3"), Source);
        events.RaiseResult(new TestResult(tc) { Outcome = TestOutcome.Skipped });
        events.RaiseComplete(1);

        var result = Parse((await ide.Lines.WaitAsync(TimeSpan.FromSeconds(10)))[1]);
        result.GetProperty("managedType").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("managedMethod").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("fqn").GetString().Should().Be("Ns.Cls.Method(\"a\")");
        result.GetProperty("outcome").GetString().Should().Be("Skipped");
    }

    [Fact]
    public async Task Values_needing_json_escaping_round_trip()
    {
        using var ide = new FakeIde();
        var events = new FakeEvents();
        new ReqnrollIdeTestLogger().Initialize(events, new Dictionary<string, string?> { [ReqnrollIdeTestLogger.EndpointParameter] = ide.Endpoint });

        var nasty = "quote \" backslash \\ newline \n tab \t unicode é 日本 control \u0001 end";
        var tc = Case("M", nasty, "M()");
        events.RaiseResult(Result(tc, TestOutcome.Failed, stdout: nasty, error: nasty));
        events.RaiseComplete(1);

        var result = Parse((await ide.Lines.WaitAsync(TimeSpan.FromSeconds(10)))[1]);
        result.GetProperty("displayName").GetString().Should().Be(nasty);
        result.GetProperty("stdout").GetString().Should().Be(nasty);
        result.GetProperty("errorMessage").GetString().Should().Be(nasty);
    }

    [Theory]
    [InlineData("127.0.0.1:5000", true)]
    [InlineData("[::1]:5000", true)]
    [InlineData("localhost:5000", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1:", false)]
    [InlineData("127.0.0.1:0", false)]
    [InlineData("127.0.0.1:70000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Endpoint_parsing_accepts_ip_port_only(string? endpoint, bool expected)
        => OutcomeSink.TryParseEndpoint(endpoint, out _, out _).Should().Be(expected);

    [Fact]
    public void NdjsonWriter_emits_one_valid_json_object_per_line()
    {
        var line = NdjsonWriter.Object("t")
            .Field("s", "a\"b")
            .Field("n", (string?)null)
            .Field("i", 42L)
            .Field("d", 1.5)
            .Field("nan", double.NaN)
            .Field("b", true)
            .Field("arr", new[] { "x", "y\n" })
            .ToLine();

        line.Should().EndWith("}\n");
        line.TrimEnd('\n').Should().NotContain("\n");
        var obj = Parse(line);
        obj.GetProperty("type").GetString().Should().Be("t");
        obj.GetProperty("s").GetString().Should().Be("a\"b");
        obj.GetProperty("n").ValueKind.Should().Be(JsonValueKind.Null);
        obj.GetProperty("i").GetInt64().Should().Be(42);
        obj.GetProperty("d").GetDouble().Should().Be(1.5);
        obj.GetProperty("nan").ValueKind.Should().Be(JsonValueKind.Null);
        obj.GetProperty("b").GetBoolean().Should().BeTrue();
        obj.GetProperty("arr").EnumerateArray().Select(e => e.GetString()).Should().Equal("x", "y\n");
    }
}
