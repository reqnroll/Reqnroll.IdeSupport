using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class ServerLifecycleReporterTests
{
    [Fact]
    public void Report_transmits_the_event_with_a_closed_reason_and_attempt_number()
    {
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        var sut = new ServerLifecycleReporter { Transmitter = transmitter };

        sut.Report(TelemetryEvents.ServerStartFailed, ServerFailureReason.ExecutableNotFound, 1);

        transmitter.Received(1).TransmitEvent(Arg.Is<ITelemetryEvent>(e =>
            e.EventName == "ServerStartFailed"
            && (string)e.Properties["Reason"] == "ExecutableNotFound"
            && (int)e.Properties["AttemptNumber"] == 1
            && e.Properties.Count == 2));
    }

    [Fact]
    public void Events_reported_before_a_transmitter_exists_are_sent_in_order_once_it_is_assigned()
    {
        var sut = new ServerLifecycleReporter();
        sut.Report(TelemetryEvents.ServerStartFailed, ServerFailureReason.StartFailed, 1);
        sut.Report(TelemetryEvents.ServerRestarted, ServerFailureReason.StartFailed, 2);
        var sent = new List<string>();
        var transmitter = Substitute.For<ITelemetryTransmitter>();
        transmitter.When(t => t.TransmitEvent(Arg.Any<ITelemetryEvent>())).Do(c => sent.Add(c.Arg<ITelemetryEvent>().EventName));

        sut.Transmitter = transmitter;
        sut.Report(TelemetryEvents.ServerExitedUnexpectedly, ServerFailureReason.ProcessExited, 2);

        sent.Should().Equal("ServerStartFailed", "ServerRestarted", "ServerExitedUnexpectedly");
    }

    [Fact]
    public void Pending_events_are_flushed_only_once()
    {
        var sut = new ServerLifecycleReporter();
        sut.Report(TelemetryEvents.ServerStartFailed, ServerFailureReason.StartFailed, 1);
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
            sut.Report(TelemetryEvents.ServerStartFailed, ServerFailureReason.StartFailed, attempt);
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

        var act = () => sut.Report(TelemetryEvents.ServerRestarted, ServerFailureReason.SessionEnded, 2);

        act.Should().NotThrow();
    }
}
