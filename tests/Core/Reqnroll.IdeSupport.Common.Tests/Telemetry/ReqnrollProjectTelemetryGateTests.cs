using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class ReqnrollProjectTelemetryGateTests
{
    private readonly ReqnrollProjectTelemetryGate _sut = new();
    private readonly List<string> _sent = new();

    private void Send(string eventName) => _sut.Send(eventName, () => _sent.Add(eventName));

    [Fact]
    public void Holds_ordinary_events_until_a_Reqnroll_project_is_reported()
    {
        Send(TelemetryEvents.LifecycleExtensionLoaded);
        Send(TelemetryEvents.ServerSessionStarted);

        _sent.Should().BeEmpty();
        _sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Opening_sends_the_held_events_in_order_and_later_events_directly()
    {
        Send(TelemetryEvents.LifecycleExtensionLoaded);
        Send(TelemetryEvents.ServerSessionStarted);

        _sut.Open();
        Send(TelemetryEvents.ProjectOpened);

        _sent.Should().Equal(
            TelemetryEvents.LifecycleExtensionLoaded,
            TelemetryEvents.ServerSessionStarted,
            TelemetryEvents.ProjectOpened);
        _sut.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Opening_twice_sends_the_held_events_once()
    {
        Send(TelemetryEvents.LifecycleExtensionLoaded);

        _sut.Open();
        _sut.Open();

        _sent.Should().ContainSingle();
    }

    [Theory]
    [InlineData(TelemetryEvents.ErrorUnhandledException)]
    [InlineData(TelemetryEvents.ErrorVsWellKnownIdsMismatch)]
    [InlineData(TelemetryEvents.LifecycleExtensionInstalled)]
    [InlineData(TelemetryEvents.LifecycleExtensionUpgraded)]
    [InlineData(TelemetryEvents.ServerProcessStartFailed)]
    [InlineData(TelemetryEvents.ServerProcessExitedUnexpectedly)]
    [InlineData(TelemetryEvents.ServerProcessConnectionLost)]
    public void Errors_install_upgrade_and_server_start_failures_are_never_held(string eventName)
    {
        Send(eventName);

        _sent.Should().Equal(eventName);
    }

    [Fact]
    public void A_restart_loaded_and_the_usage_heartbeat_are_held_like_any_other_event()
    {
        ReqnrollProjectTelemetryGate.IsAlwaysAllowed(TelemetryEvents.ServerProcessRestarted).Should().BeFalse();
        ReqnrollProjectTelemetryGate.IsAlwaysAllowed(TelemetryEvents.LifecycleExtensionLoaded).Should().BeFalse();
        ReqnrollProjectTelemetryGate.IsAlwaysAllowed(TelemetryEvents.LifecycleUsageRecorded).Should().BeFalse();
    }

    [Fact]
    public void An_event_without_a_name_is_held_rather_than_throwing()
    {
        var sent = false;

        _sut.Invoking(s => s.Send(null, () => sent = true)).Should().NotThrow();
        sent.Should().BeFalse();

        _sut.Open();
        sent.Should().BeTrue();
    }

    [Fact]
    public void Holds_at_most_the_cap_and_drops_the_rest()
    {
        for (var i = 0; i < ReqnrollProjectTelemetryGate.MaxHeldEvents + 5; i++)
            Send(TelemetryEvents.PerfSample);

        _sut.Open();

        _sent.Should().HaveCount(ReqnrollProjectTelemetryGate.MaxHeldEvents);
    }

    [Fact]
    public void A_failing_held_send_does_not_stop_the_others()
    {
        _sut.Send(TelemetryEvents.LifecycleExtensionLoaded, () => throw new InvalidOperationException());
        Send(TelemetryEvents.ServerSessionStarted);

        _sut.Invoking(s => s.Open()).Should().NotThrow();

        _sent.Should().Equal(TelemetryEvents.ServerSessionStarted);
    }

    [Fact]
    public void A_reset_closes_an_open_gate_until_the_next_server_reports_a_Reqnroll_project()
    {
        _sut.Open();

        _sut.Reset();
        Send(TelemetryEvents.ServerSessionStarted);

        _sent.Should().BeEmpty();
        _sut.IsOpen.Should().BeFalse();

        _sut.Open();
        _sent.Should().Equal(TelemetryEvents.ServerSessionStarted);
    }

    [Fact]
    public void A_reset_drops_what_the_previous_session_held()
    {
        Send(TelemetryEvents.ProjectOpened);

        _sut.Reset();
        Send(TelemetryEvents.ServerSessionStarted);
        _sut.Open();

        _sent.Should().Equal(TelemetryEvents.ServerSessionStarted);
    }

    [Fact]
    public void A_reset_still_lets_errors_through()
    {
        _sut.Open();
        _sut.Reset();

        Send(TelemetryEvents.ErrorUnhandledException);

        _sent.Should().Equal(TelemetryEvents.ErrorUnhandledException);
    }

    [Fact]
    public async Task A_wait_started_after_a_reset_completes_on_the_next_open()
    {
        _sut.Open();
        _sut.Reset();

        var wait = _sut.WaitUntilOpenAsync(CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        _sut.Open();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_pending_wait_survives_a_reset()
    {
        var wait = _sut.WaitUntilOpenAsync(CancellationToken.None);

        _sut.Reset();
        _sut.Open();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_completes_when_the_gate_opens()
    {
        var wait = _sut.WaitUntilOpenAsync(CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        _sut.Open();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var wait = _sut.WaitUntilOpenAsync(cts.Token);

        cts.Cancel();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
    }
}
