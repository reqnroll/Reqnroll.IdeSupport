using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// <see cref="TestRunConnection"/> driven with parsed messages and no socket: the per-connection
/// contract (running marks, idle connections, aborted-run telemetry, reporter-kind inference).
/// </summary>
public class TestRunConnectionTests
{
    private readonly TestOutcomeStore _store = new();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();
    private readonly List<Dictionary<string, object?>> _runCompleted = new();
    private int _seq;

    public TestRunConnectionTests()
    {
        _telemetry
            .When(t => t.SendEvent(TelemetryEvents.ProjectTestRunCompleted, Arg.Any<Dictionary<string, object?>>()))
            .Do(ci => _runCompleted.Add(ci.ArgAt<Dictionary<string, object?>>(1)));
    }

    private TestRunConnection NewConnection()
        => new(_store, _logger, NullOperationDurationRecorder.Instance, _telemetry, () => ++_seq);

    private static JObject Hello(string runId = "run-1", int protocol = 1, string? reporterKind = null, bool withTfm = true)
    {
        var hello = new JObject { ["type"] = "hello", ["protocol"] = protocol, ["runId"] = runId, ["runnerPid"] = 1 };
        if (withTfm) hello["targetFramework"] = ".NETCoreApp,Version=v8.0";
        if (reporterKind is not null) hello["reporterKind"] = reporterKind;
        return hello;
    }

    private static JObject RunStart(int testCount = 1) => new() { ["type"] = "runStart", ["testCount"] = testCount };

    private static JObject Result(string method, string outcome) => new()
    {
        ["type"] = "result",
        ["source"] = @"C:\repo\Specs.dll",
        ["managedType"] = "Specs.CalcFeature",
        ["managedMethod"] = method + "(System.String)",
        ["fqn"] = "Specs.CalcFeature." + method,
        ["displayName"] = method,
        ["outcome"] = outcome,
    };

    private static JObject RunComplete(int executed = 1, bool aborted = false, bool canceled = false)
        => new() { ["type"] = "runComplete", ["executed"] = executed, ["aborted"] = aborted, ["canceled"] = canceled };

    [Fact]
    public void Hello_only_connection_is_ignored_on_close()
    {
        var connection = NewConnection();

        connection.OnHello(Hello()).Should().BeTrue();
        connection.OnClosed().Should().BeFalse();

        _runCompleted.Should().BeEmpty();
        connection.RunId.Should().Be("run-1");
    }

    [Fact]
    public void Non_hello_first_line_is_rejected()
    {
        var connection = NewConnection();

        connection.OnHello(RunStart()).Should().BeFalse();
        connection.OnClosed().Should().BeFalse();
        connection.RunId.Should().BeNull();
    }

    [Fact]
    public void Normal_run_stores_results_and_sends_one_completed_telemetry_event()
    {
        var connection = NewConnection();
        connection.OnHello(Hello(reporterKind: "VSTestLogger"));
        connection.OnMessage(RunStart(3));
        connection.OnMessage(Result("A", "Passed"));
        connection.OnMessage(Result("B", "Failed"));
        connection.OnMessage(Result("C", "Skipped"));
        connection.OnMessage(RunComplete(executed: 3));

        connection.OnClosed().Should().BeTrue();

        connection.ResultCount.Should().Be(3);
        _runCompleted.Should().ContainSingle();
        var evt = _runCompleted[0];
        evt["ResultCount"].Should().Be(3);
        evt["PassedCount"].Should().Be(1);
        evt["FailedCount"].Should().Be(1);
        evt["SkippedCount"].Should().Be(1);
        evt["ExecutedCount"].Should().Be(3);
        evt["CompletedNormally"].Should().Be(true);
        evt["ReporterKind"].Should().Be("VSTestLogger");
    }

    [Fact]
    public void Run_dropped_after_results_reports_aborted_telemetry_and_asks_for_persistence()
    {
        var connection = NewConnection();
        connection.OnHello(Hello());
        connection.OnMessage(RunStart());
        connection.OnMessage(Result("A", "Passed"));

        connection.OnClosed().Should().BeTrue();

        _runCompleted.Should().ContainSingle();
        _runCompleted[0]["Aborted"].Should().Be(true);
        _runCompleted[0]["CompletedNormally"].Should().Be(false);
        _runCompleted[0]["ExecutedCount"].Should().Be(1);
    }

    [Fact]
    public void Run_dropped_before_any_result_still_reports_aborted_when_it_had_started()
    {
        var connection = NewConnection();
        connection.OnHello(Hello());
        connection.OnMessage(RunStart());

        connection.OnClosed().Should().BeFalse();

        _runCompleted.Should().ContainSingle();
        _runCompleted[0]["Aborted"].Should().Be(true);
    }

    [Theory]
    [InlineData(true, "VSTestLogger")]
    [InlineData(false, "MTP")]
    public void Legacy_hello_without_reporterKind_infers_it_from_targetFramework(bool withTfm, string expected)
    {
        var connection = NewConnection();
        connection.OnHello(Hello(withTfm: withTfm));
        connection.OnMessage(RunStart());
        connection.OnMessage(RunComplete());

        _runCompleted.Should().ContainSingle();
        _runCompleted[0]["ReporterKind"].Should().Be(expected);
    }

    [Fact]
    public void Connections_sharing_a_run_id_do_not_clear_each_others_running_marks()
    {
        var real = NewConnection();
        var idle = NewConnection();
        real.OnHello(Hello("shared"));
        idle.OnHello(Hello("shared"));
        var key = TestOutcomeKey.From(@"C:\repo\Specs.dll", "Specs.CalcFeature", "A(System.String)", "Specs.CalcFeature.A")!;
        var packed = string.Join(TestOutcomeTcpListener.TestIdentitySeparator, @"C:\repo\Specs.dll", "Specs.CalcFeature", "A(System.String)", "Specs.CalcFeature.A", "A");
        real.OnMessage(new JObject { ["type"] = "runStart", ["tests"] = new JArray(packed) });
        (_store.TryGet(key)?.IsRunning ?? false).Should().BeTrue();

        idle.OnClosed();

        (_store.TryGet(key)?.IsRunning ?? false).Should().BeTrue();
        real.OnClosed();
        (_store.TryGet(key)?.IsRunning ?? false).Should().BeFalse();
    }

    [Fact]
    public void Phase_moves_forward_through_the_lifecycle()
    {
        var connection = NewConnection();
        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.AwaitingHello);

        connection.OnHello(Hello());
        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.Idle);

        connection.OnMessage(RunStart());
        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.Running);

        connection.OnMessage(RunComplete());
        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.Completed);

        connection.OnMessage(RunStart());
        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.Completed);
    }

    [Fact]
    public void A_stored_result_without_runStart_makes_the_connection_a_running_one()
    {
        var connection = NewConnection();
        connection.OnHello(Hello());

        connection.OnMessage(Result("A", "Passed"));

        connection.Phase.Should().Be(TestRunConnection.ConnectionPhase.Running);
    }
}
