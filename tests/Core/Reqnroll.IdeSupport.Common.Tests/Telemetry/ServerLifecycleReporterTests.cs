using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class ServerLifecycleReporterTests
{
    [Fact]
    public void Report_transmits_the_event_with_a_closed_reason_and_attempt_number()
    {
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        var sut = new ServerLifecycleReporter { Transmitter = transmitter };

        sut.Report(TelemetryEvents.ServerProcessStartFailed, ServerFailureReason.ExecutableNotFound, 1);

        transmitter.Received(1).TransmitEvent(Arg.Is<ITelemetryEvent>(e =>
            e.EventName == "Server.Process.StartFailed"
            && (string)e.Properties["Reason"] == "ExecutableNotFound"
            && (int)e.Properties["AttemptNumber"] == 1
            && e.Properties.Count == 2));
    }

    [Fact]
    public void Events_reported_before_a_transmitter_exists_are_sent_in_order_once_it_is_assigned()
    {
        var sut = new ServerLifecycleReporter();
        sut.Report(TelemetryEvents.ServerProcessStartFailed, ServerFailureReason.StartFailed, 1);
        sut.Report(TelemetryEvents.ServerProcessRestarted, ServerFailureReason.StartFailed, 2);
        var sent = new List<string>();
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        transmitter.When(t => t.TransmitEvent(Arg.Any<ITelemetryEvent>())).Do(c => sent.Add(c.Arg<ITelemetryEvent>().EventName));

        sut.Transmitter = transmitter;
        sut.Report(TelemetryEvents.ServerProcessExitedUnexpectedly, ServerFailureReason.ProcessExited, 2);

        sent.Should().Equal("Server.Process.StartFailed", "Server.Process.Restarted", "Server.Process.ExitedUnexpectedly");
    }

    [Fact]
    public void Pending_events_are_flushed_only_once()
    {
        var sut = new ServerLifecycleReporter();
        sut.Report(TelemetryEvents.ServerProcessStartFailed, ServerFailureReason.StartFailed, 1);
        var transmitter = Substitute.For<ITelemetryTransmitter>();

        sut.Transmitter = transmitter;
        sut.Transmitter = transmitter;

        transmitter.Received(1).TransmitEvent(Arg.Any<ITelemetryEvent>());
    }

    [Fact]
    public void The_pending_queue_is_bounded_and_drops_the_oldest()
    {
        var sut = new ServerLifecycleReporter();
        for (var attempt = 1; attempt <= 25; attempt++)
            sut.Report(TelemetryEvents.ServerProcessStartFailed, ServerFailureReason.StartFailed, attempt);
        var attempts = new List<int>();
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        transmitter.When(t => t.TransmitEvent(Arg.Any<ITelemetryEvent>()))
            .Do(c => attempts.Add((int)c.Arg<ITelemetryEvent>().Properties["AttemptNumber"]));

        sut.Transmitter = transmitter;

        attempts.Should().HaveCount(20).And.StartWith(6);
    }

    [Fact]
    public void A_throwing_transmitter_never_propagates()
    {
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        transmitter.When(t => t.TransmitEvent(Arg.Any<ITelemetryEvent>())).Do(_ => throw new InvalidOperationException("boom"));
        var sut = new ServerLifecycleReporter { Transmitter = transmitter };

        var act = () => sut.Report(TelemetryEvents.ServerProcessRestarted, ServerFailureReason.SessionEnded, 2);

        act.Should().NotThrow();
    }

    [Fact]
    public void Details_are_added_to_the_event_but_never_replace_the_reason_or_attempt_number()
    {
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        var sut = new ServerLifecycleReporter { Transmitter = transmitter };
        var details = new Dictionary<string, object>
        {
            ["ServerSessionId"] = "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00",
            ["ExitCode"] = -532462766,
            ["Reason"] = "ignored",
        };

        sut.Report(TelemetryEvents.ServerProcessExitedUnexpectedly, ServerFailureReason.ProcessExited, 3, details);

        transmitter.Received(1).TransmitEvent(Arg.Is<ITelemetryEvent>(e =>
            (string)e.Properties["Reason"] == "ProcessExited"
            && (int)e.Properties["AttemptNumber"] == 3
            && (string)e.Properties["ServerSessionId"] == "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00"
            && (int)e.Properties["ExitCode"] == -532462766
            && e.Properties.Count == 4));
    }

    [Fact]
    public void DescribeProcess_without_a_process_reports_only_the_session_id()
    {
        var details = ServerLifecycleTelemetry.DescribeProcess(null, "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00", DateTime.Now);

        details.Should().BeEquivalentTo(new Dictionary<string, object> { ["ServerSessionId"] = "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00" });
    }

    [Fact]
    public void DescribeProcess_omits_a_missing_session_id()
    {
        ServerLifecycleTelemetry.DescribeProcess(null, null, DateTime.Now).Should().BeEmpty();
    }

    [Fact]
    public void DescribeProcess_of_a_running_process_reports_uptime_but_no_exit_code()
    {
        using var current = Process.GetCurrentProcess();

        var details = ServerLifecycleTelemetry.DescribeProcess(current, null, DateTime.Now);

        details.Should().NotContainKey("ExitCode");
        ((long)details["UptimeMs"]).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void DescribeProcess_of_an_exited_process_reports_its_exit_code_and_uptime()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var details = ServerLifecycleTelemetry.DescribeProcess(process, "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00", DateTime.Now);

        details["ExitCode"].Should().Be(process.ExitCode);
        ((long)details["UptimeMs"]).Should().BeGreaterThanOrEqualTo(0);
        details["ServerSessionId"].Should().Be("6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00");
    }

    [Fact]
    public void DescribeProcess_of_an_unstarted_process_object_never_throws()
    {
        using var process = new Process();

        var details = ServerLifecycleTelemetry.DescribeProcess(process, null, DateTime.Now);

        details.Should().BeEmpty();
    }
}

public class ServerSessionTests
{
    [Fact]
    public void NewId_returns_a_distinct_canonical_guid()
    {
        var id = ServerSession.NewId();

        Guid.TryParse(id, out _).Should().BeTrue();
        id.Should().Be(Guid.Parse(id).ToString("D")).And.NotBe(ServerSession.NewId());
    }

    [Theory]
    [InlineData("6F1C2B8E-0D3A-4C55-9B7E-2A4D8C1E9F00", "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00")]
    [InlineData("{6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00}", "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00")]
    [InlineData("not-a-guid", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_accepts_only_guids(string? value, string? expected)
    {
        ServerSession.Normalize(value).Should().Be(expected);
    }
}
