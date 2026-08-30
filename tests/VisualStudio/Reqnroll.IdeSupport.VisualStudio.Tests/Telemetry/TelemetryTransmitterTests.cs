using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
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
    private IEnableTelemetryChecker _enableTelemetryCheckerStub;
    private readonly CapturingDebugLog _debugLog = new();

    [Fact]
    public void Should_NotSendTelemetry_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitEvent(Substitute.For<ITelemetryEvent>());

        _enableTelemetryCheckerStub.Received(1).IsEnabled();
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
    }

    [Fact]
    public void Should_SendTelemetry_WhenEnabled()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitEvent(Substitute.For<ITelemetryEvent>());

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

        sut.TransmitEvent(new GenericEvent(eventName));

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

        var exception = Record.Exception(() => sut.TransmitEvent(Substitute.For<ITelemetryEvent>()));

        Assert.Null(exception);
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
        return new VsTelemetryTransmitter(telemetryClient, _enableTelemetryCheckerStub, null, _debugLog);
    }

    // ── Debug-log mirror (host side) ──────────────────────────────────────────────

    [Fact]
    public void Should_MirrorToDebugLog_AsHost_WhenDisabled()
    {
        var sut = CreateSut();
        GivenTelemetryDisabled();

        sut.TransmitEvent(new GenericEvent("Extension loaded"));

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

        sut.TransmitEvent(new GenericEvent("Extension loaded"));

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

        sut.TransmitEvent(new GenericEvent("Extension loaded"));

        _debugLog.Records.Should().Contain(r => r.Transmitted == false && r.Error != null);
    }

    [Fact]
    public void Should_MirrorExceptionTelemetryToDebugLog()
    {
        var sut = CreateSut();

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        var rec = _debugLog.Records.Should().ContainSingle().Which;
        rec.Source.Should().Be("host");
        rec.Event.Should().Contain("InvalidOperationException");
        rec.Enabled.Should().BeNull();      // exception path is not gated by the opt-out checker
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
        _telemetryChannel.ThrowOnSend = true;

        sut.TransmitFatalExceptionEvent(new InvalidOperationException("boom"), isFatal: true);

        var rec = _debugLog.Records.Should().ContainSingle().Which;
        rec.Transmitted.Should().BeFalse();
        rec.Error.Should().NotBeNull();
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
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated AppInsights failure");
        SentTelemtries.Add(item);
    }

    public void Flush() => IsFlushed = true;
    public void Dispose() { }
}
