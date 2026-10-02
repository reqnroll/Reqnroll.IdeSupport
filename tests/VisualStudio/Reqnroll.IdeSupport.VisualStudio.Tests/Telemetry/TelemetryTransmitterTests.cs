using System.Collections.Immutable;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Telemetry;
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
    private readonly List<string> _logLines = new();
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
    [InlineData("Extension loaded")]
    [InlineData("Extension installed")]
    [InlineData("100 day usage")]
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
            .And.Contain("ExtensionVersion", "1.2.3");
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

    private VsTelemetryTransmitter CreateSut()
    {
        _enableTelemetryCheckerStub = Substitute.For<IEnableTelemetryChecker>();
        _telemetryChannel = new InMemoryTelemetryChannel();
        var config = new TelemetryConfiguration
        {
            TelemetryChannel = _telemetryChannel,
            ConnectionString = $"InstrumentationKey={Guid.NewGuid():N}"
        };
        var telemetryClient = new TelemetryClient(config);
        var logger = Substitute.For<IIdeSupportLogger>();
        logger.Level.Returns(System.Diagnostics.TraceLevel.Verbose);
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(c =>
        {
            var m = c.Arg<LogMessage>();
            if (m.Level == System.Diagnostics.TraceLevel.Info) _logLines.Add(m.Message);
        });
        return new VsTelemetryTransmitter(telemetryClient, _enableTelemetryCheckerStub, logger, _debugLog);
    }

    // ── Unreachable endpoint (#859) ───────────────────────────────────────────────

    [Fact]
    public void Should_NotProduceSecondEvent_WhenTransmissionFails()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

        _telemetryChannel.SendAttempts.Should().Be(1);
        _debugLog.Records.Should().ContainSingle(r => r.Transmitted == false && r.Error != null);
    }

    [Fact]
    public void Should_StopTouchingTheNetwork_AndLogOnce_AfterFirstFailure()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        for (var i = 0; i < 25; i++)
            sut.TransmitEvent(new VsGenericEvent("Extension loaded"));
        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        _telemetryChannel.SendAttempts.Should().Be(1);
        _logLines.Should().ContainSingle().Which.Should().Be(
            "Telemetry endpoint unreachable; telemetry for this session will be dropped");
        // every dropped attempt is still mirrored to the debug log, with transmitted:false
        _debugLog.Records.Should().HaveCount(26).And.OnlyContain(r => r.Transmitted == false);
    }

    [Fact]
    public async Task Should_NotFlush_OnDispose_WhenBreakerOpen()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;
        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

        await sut.DisposeAsync();

        _telemetryChannel.IsFlushed.Should().BeFalse();
    }

    [Fact]
    public async Task Should_BoundDispose_WhenFlushBlackHoled()
    {
        var sut = CreateSut();
        using var gate = new System.Threading.ManualResetEventSlim(false);
        _telemetryChannel.BlockFlush = gate;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.DisposeAsync();
        sw.Stop();
        gate.Set();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        _logLines.Should().ContainSingle();
    }

    // ── Debug-log mirror (host side) ──────────────────────────────────────────────

    [Fact]
    public void Should_MirrorToDebugLog_AsHost_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

        // Not transmitted (opted out) but still mirrored for debugging.
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
        _debugLog.Records.Should().ContainSingle();
        var rec = _debugLog.Records[0];
        rec.Source.Should().Be("host");
        rec.Event.Should().Be("Extension loaded");
        rec.Enabled.Should().BeFalse();
        rec.Transmitted.Should().BeFalse();
        rec.Error.Should().BeNull();
    }

    [Fact]
    public void Should_MirrorToDebugLog_AsTransmitted_WhenEnabled()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
        _debugLog.Records.Should().ContainSingle();
        var rec = _debugLog.Records[0];
        rec.Source.Should().Be("host");
        rec.Enabled.Should().BeTrue();
        rec.Transmitted.Should().BeTrue();
        rec.Error.Should().BeNull();
    }

    [Fact]
    public void Should_MirrorErrorToDebugLog_WhenTransmissionFails()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

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

public class InMemoryTelemetryChannel : ITelemetryChannel
{
    public List<ITelemetry> SentTelemtries { get; } = new();
    public bool IsFlushed { get; private set; }
    public bool ThrowOnSend { get; set; }
    public bool? DeveloperMode { get; set; }
    public string EndpointAddress { get; set; }

    public void Send(ITelemetry item)
    {
        SendAttempts++;
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated AppInsights failure");
        SentTelemtries.Add(item);
    }

    public int SendAttempts { get; private set; }
    public System.Threading.ManualResetEventSlim BlockFlush { get; set; }

    public void Flush()
    {
        BlockFlush?.Wait();
        IsFlushed = true;
    }
    public void Dispose() { }
}
