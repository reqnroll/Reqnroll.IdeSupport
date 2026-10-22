using System.Collections.Immutable;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.IdeServices;
using VsTelemetryTransmitter = Reqnroll.IdeSupport.VisualStudio.Telemetry.TelemetryTransmitter;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Telemetry;

// Lives in the VS test project (not Common.Tests) because the concrete TelemetryTransmitter
// and its Microsoft.ApplicationInsights dependency now live in VSSDKIntegration. Common only
// owns the IDE-neutral contracts. The SUT is constructed through its internal test-seam ctor
// (InternalsVisibleTo from VSSDKIntegration), injecting a TelemetryClient backed by an
// in-memory channel so we can assert what was transmitted without contacting App Insights.
public class TelemetryTransmitterTests
{
    private InMemoryTelemetryChannel _telemetryChannel;
    // VS writes no user-facing notice when the endpoint is unreachable, and its "flush did not
    // finish" notes are verbose-only, so both levels are captured separately.
    private readonly List<string> _infoLogLines = new();
    private readonly List<string> _verboseLogLines = new();
    private IEnableTelemetryChecker _enableTelemetryCheckerStub;
    private readonly CapturingDebugLog _debugLog = new();

    [Fact]
    public void Should_NotSendTelemetry_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitEvent(FakeTelemetryEvent());

        _enableTelemetryCheckerStub.Received(1).IsEnabled();
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
    }

    [Fact]
    public void Should_SendTelemetry_WhenEnabled()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitEvent(FakeTelemetryEvent());

        _enableTelemetryCheckerStub.Received(1).IsEnabled();
        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Lifecycle.Extension.Loaded")]
    [InlineData("Lifecycle.Extension.Installed")]
    [InlineData("Lifecycle.Usage.Recorded")]
    public void Should_TransmitEvents(string eventName)
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitEvent(new VsGenericEvent(eventName));

        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
        _telemetryChannel.SentTelemtries.Single()
            .Should().BeOfType<EventTelemetry>()
            .Which.Name.Should().Be(eventName);
    }

    [Fact]
    public async Task Should_FlushOnDispose()
    {
        var sut = CreateSut();

        await sut.DisposeAsync();

        _telemetryChannel.IsFlushed.Should().BeTrue();
    }

    [Fact]
    public void Should_NotThrow_WhenAppInsightsFails()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        _telemetryChannel.ThrowOnSend = true;

        var exception = Record.Exception(() => sut.TransmitEvent(FakeTelemetryEvent()));

        Assert.Null(exception);
    }

    [Fact]
    public void Should_StampCanonicalClientIdentity_OnEveryEvent()
    {
        var versions = Substitute.For<IVersionProvider>();
        versions.GetVsVersion().Returns("17.14");
        versions.GetExtensionVersion().Returns("1.2.3");
        var properties = new Dictionary<string, string>();

        VsTelemetryTransmitter.ApplyClientIdentity(properties, versions);

        properties.Should().Contain("IdeClient", "visualstudio")
            .And.Contain("Ide", "Microsoft Visual Studio")
            .And.Contain("IdeVersion", "17.14")
            .And.Contain("ExtensionVersion", "1.2.3")
            .And.Contain("ClientSessionId", ClientSession.Id);
    }

    [Fact]
    public void ApplyPrivacyDefaults_replaces_the_machine_name_on_events_and_exceptions()
    {
        var channel = new InMemoryTelemetryChannel();
        var config = new TelemetryConfiguration
        {
            TelemetryChannel = channel,
            ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
        };
        var client = new TelemetryClient(config);
        VsTelemetryTransmitter.ApplyPrivacyDefaults(client.Context);

        client.TrackEvent(new EventTelemetry("probe"));
        client.TrackException(new ExceptionTelemetry(new InvalidOperationException("boom")));

        channel.SentTelemtries.Should().HaveCount(2);
        channel.SentTelemtries.Select(t => t.Context.Cloud.RoleInstance)
            .Should().OnlyContain(r => r == VsTelemetryTransmitter.RedactedRoleInstance)
            .And.NotContain(Environment.MachineName);
    }

    // NSubstitute doesn't auto-populate ImmutableDictionary-typed members with an empty
    // instance the way it does for common collection interfaces, so a bare
    // Substitute.For<ITelemetryEvent>() has a null Properties and blows up in the
    // foreach inside TransmitEvent.
    private static ITelemetryEvent FakeTelemetryEvent()
    {
        var telemetryEvent = Substitute.For<ITelemetryEvent>();
        telemetryEvent.Properties.Returns(ImmutableDictionary<string, object>.Empty);
        return telemetryEvent;
    }

    private void GivenTelemetryEnabled()
    {
        _enableTelemetryCheckerStub.IsEnabled().Returns(true);
    }

    private void GivenTelemetryDisabled()
    {
        _enableTelemetryCheckerStub.IsEnabled().Returns(false);
    }

    private VsTelemetryTransmitter CreateSut(Action<TelemetryClient>? configureClient = null)
    {
        _enableTelemetryCheckerStub = Substitute.For<IEnableTelemetryChecker>();
        _telemetryChannel = new InMemoryTelemetryChannel();
        var config = new TelemetryConfiguration
        {
            TelemetryChannel = _telemetryChannel,
            ConnectionString = $"InstrumentationKey={Guid.NewGuid():N}"
        };
        var telemetryClient = new TelemetryClient(config);
        configureClient?.Invoke(telemetryClient);
        var logger = Substitute.For<IIdeSupportLogger>();
        logger.Level.Returns(System.Diagnostics.TraceLevel.Verbose);
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(c =>
        {
            var m = c.Arg<LogMessage>();
            if (m.Level == System.Diagnostics.TraceLevel.Info) _infoLogLines.Add(m.Message);
            else if (m.Level == System.Diagnostics.TraceLevel.Verbose) _verboseLogLines.Add(m.Message);
        });
        return new VsTelemetryTransmitter(telemetryClient, _enableTelemetryCheckerStub, logger, _debugLog);
    }

    // ── Unreachable endpoint ───────────────────────────────────────────────
    //
    // Visual Studio's contract differs from VS Code's and Rider's. The Application Insights channel
    // hands events to a background sender and swallows the outcome (ETW/self-diagnostics only), so
    // this class never learns that a send failed: there is no breaker here and no "telemetry is
    // being dropped" notice. These tests pin the behaviour that actually ships — silent drop, no
    // self-amplifying second event, and a flush at shutdown that is bounded rather than blocking
    // the UI thread for the SDK's 100 s HTTP timeout.

    [Fact]
    public void Should_NotTransmitASecondEvent_WhenTheChannelThrowsOnSend()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));

        // One hand-off only: a failed transmission is never re-reported as an exception event to
        // the same unreachable endpoint.
        _telemetryChannel.SendAttempts.Should().Be(1);
        _debugLog.Records.Should().ContainSingle(r => r.Transmitted == false && r.Error != null);
    }

    [Fact]
    public void Should_DropEventsSilently_WhenTheEndpointIsUnreachable()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.SimulateUnreachableEndpoint = true;

        for (var i = 0; i < 25; i++)
            sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));
        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        // No notice: the host cannot see the failure, so it must not claim it can.
        _infoLogLines.Should().BeEmpty();
        // Exactly one hand-off per event: no retry storm, no feedback event, nothing delivered.
        _telemetryChannel.SendAttempts.Should().Be(26);
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
        // Hand-off (not delivery) is still mirrored for debugging — the SDK gives no better signal.
        _debugLog.Records.Should().HaveCount(26).And.OnlyContain(r => r.Transmitted == true);
    }

    [Fact]
    public async Task Should_BoundDispose_WhenTheFlushIsBlackHoled()
    {
        var sut = CreateSut();
        using var gate = new System.Threading.ManualResetEventSlim(false);
        _telemetryChannel.BlockFlush = gate;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.DisposeAsync();
        sw.Stop();
        gate.Set();

        // Bounded, not announced: shutdown waits at most FlushTimeout, then abandons the flush.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        _infoLogLines.Should().BeEmpty();
        _verboseLogLines.Should().Contain(line => line.Contains("did not finish"));
    }

    [Fact]
    public async Task Should_NotThrow_WhenTheFlushFails()
    {
        var sut = CreateSut();
        _telemetryChannel.ThrowOnFlush = true;

        var exception = await Record.ExceptionAsync(() => sut.DisposeAsync().AsTask());

        Assert.Null(exception);
        _infoLogLines.Should().BeEmpty();
        _verboseLogLines.Should().Contain(line => line.Contains("flush failed"));
    }

    // ── Debug-log mirror (host side) ──────────────────────────────────────────────

    [Fact]
    public void Should_MirrorToDebugLog_AsHost_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));

        // Not transmitted (opted out) but still mirrored for debugging.
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
        _debugLog.Records.Should().ContainSingle();
        var rec = _debugLog.Records[0];
        rec.Source.Should().Be("host");
        rec.Event.Should().Be("Lifecycle.Extension.Loaded");
        rec.Enabled.Should().BeFalse();
        rec.Transmitted.Should().BeFalse();
        rec.Error.Should().BeNull();
    }

    [Fact]
    public void Should_MirrorToDebugLog_AsTransmitted_WhenEnabled()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));

        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
        _debugLog.Records.Should().ContainSingle();
        var rec = _debugLog.Records[0];
        rec.Source.Should().Be("host");
        rec.Enabled.Should().BeTrue();
        rec.Transmitted.Should().BeTrue();
        rec.Error.Should().BeNull();
    }

    [Fact]
    public void Should_MirrorToDebugLog_TheClientGlobalPropertiesItTransmits()
    {
        var sut = CreateSut(client => client.Context.GlobalProperties["ClientSessionId"] = "session-x");
        GivenTelemetryEnabled();

        sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));

        var props = _debugLog.Records.Should().ContainSingle().Which.Props
            .Should().BeAssignableTo<IDictionary<string, object?>>().Which;
        props.Should().Contain("ClientSessionId", "session-x");
    }

    [Fact]
    public void Should_MirrorErrorToDebugLog_WhenTransmissionFails()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitEvent(new VsGenericEvent("Lifecycle.Extension.Loaded"));

        _debugLog.Records.Should().Contain(r => r.Transmitted == false && r.Error != null);
    }

    [Fact]
    public void Should_MirrorExceptionTelemetryToDebugLog()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        var rec = _debugLog.Records.Should().ContainSingle().Which;
        rec.Source.Should().Be("host");
        rec.Event.Should().Contain("InvalidOperationException");
        rec.Enabled.Should().BeTrue();
        rec.Transmitted.Should().BeTrue();
        rec.Error.Should().BeNull();

        var props = (System.Collections.IDictionary)rec.Props;
        props["ExceptionType"].Should().Be(typeof(InvalidOperationException).FullName);
        props["Message"].Should().Be("boom");
        props["IsFatal"].Should().Be("True");
    }

    [Fact]
    public void Exceptions_are_sent_as_a_sanitised_UnhandledException_event_not_a_raw_ExceptionTelemetry()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        var inner = new ArgumentException("inner secret 'Acme.Inner'");
        Exception thrown;
        try
        {
            throw new InvalidOperationException(
                @"Could not load type 'Acme.Payroll.Steps.PaymentSteps' from C:\Users\Jane\Payroll\Salary.feature", inner);
        }
        catch (Exception ex) { thrown = ex; }

        sut.TransmitFatalExceptionEvent(thrown, isFatal: true);

        var sent = _telemetryChannel.SentTelemtries.Should().ContainSingle().Subject;
        sent.Should().BeOfType<EventTelemetry>("a raw ExceptionTelemetry would serialise the full message, stack and inner exceptions");
        var evt = (EventTelemetry)sent;
        evt.Name.Should().Be("Error.UnhandledException");
        evt.Properties["ExceptionType"].Should().Be(typeof(InvalidOperationException).FullName);
        evt.Properties["IsFatal"].Should().Be("True");
        evt.Properties["Message"].Should().Be("Could not load type <text> from <path>");
        evt.Properties.Values.Should().NotContain(v => v.Contains("Acme") || v.Contains("Jane") || v.Contains("Salary"));
    }

    [Fact]
    public void Sanitised_exception_properties_cap_the_message_and_omit_the_raw_stack()
    {
        var props = VsTelemetryTransmitter.BuildSanitizedExceptionProps(
            new InvalidOperationException(new string('x', 1000)), Array.Empty<KeyValuePair<string, object>>());

        props["Message"].Length.Should().BeLessThanOrEqualTo(TelemetryScrubber.MaxMessageLength);
        props.Should().NotContainKey("StackTrace");
    }

    [Fact]
    public void Should_MirrorException_WithError_WhenTransmissionFails()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        var rec = _debugLog.Records.Should().ContainSingle().Which;
        rec.Transmitted.Should().BeFalse();
        rec.Error.Should().NotBeNull();
    }

    [Fact]
    public void Should_NotTrackException_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        _telemetryChannel.SentTelemtries.Should().BeEmpty();
    }

    [Fact]
    public void Should_MirrorExceptionToDebugLog_AsGated_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        var rec = _debugLog.Records.Should().ContainSingle().Which;
        rec.Enabled.Should().BeFalse();
        rec.Transmitted.Should().BeFalse();
        rec.Error.Should().BeNull();
    }

    private sealed class CapturingDebugLog : ITelemetryDebugLog
    {
        public bool IsEnabled => true;
        public List<(string Source, string Event, object Props, bool? Enabled, bool? Transmitted, string Error)> Records { get; } = new();

        public void Record(string source, string eventName, object properties,
            bool? enabled = null, bool? transmitted = null, string error = null)
            => Records.Add((source, eventName, properties, enabled, transmitted, error));
    }
}

/// <summary>
/// Test double for the Application Insights channel. By default it mirrors the SDK's
/// <c>InMemoryChannel</c> as it behaves for an unreachable endpoint: <see cref="Send"/> only buffers
/// and never throws, so a dead endpoint produces no failure signal in the transmitter (the SDK
/// swallows the outcome inside <c>InMemoryTransmitter</c>).
/// <see cref="ThrowOnSend"/>/<see cref="ThrowOnFlush"/> simulate a *synchronous* channel fault, which
/// the real channel does not produce for a network failure but which the transmitter's defensive
/// catches must survive.
/// </summary>
public class InMemoryTelemetryChannel : ITelemetryChannel
{
    public List<ITelemetry> SentTelemtries { get; } = new();
    public bool IsFlushed { get; private set; }
    public bool ThrowOnSend { get; set; }
    public bool ThrowOnFlush { get; set; }
    /// <summary>Endpoint unreachable: hand-offs are counted, nothing is buffered, nothing is signalled.</summary>
    public bool SimulateUnreachableEndpoint { get; set; }
    public bool? DeveloperMode { get; set; }
    public string EndpointAddress { get; set; }

    public void Send(ITelemetry item)
    {
        SendAttempts++;
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated AppInsights failure");
        if (SimulateUnreachableEndpoint)
            return;
        SentTelemtries.Add(item);
    }

    public int SendAttempts { get; private set; }
    public System.Threading.ManualResetEventSlim BlockFlush { get; set; }

    public void Flush()
    {
        // Bounded so an abandoned flush cannot keep the test process alive.
        BlockFlush?.Wait(TimeSpan.FromSeconds(10));
        if (ThrowOnFlush)
            throw new InvalidOperationException("Simulated AppInsights flush failure");
        IsFlushed = true;
    }
    public void Dispose() { }
}
