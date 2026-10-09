using System.Collections.Immutable;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
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
    // finish" notes are verbose-only (#859 asymmetry), so both levels are captured separately.
    private readonly List<string> _infoLogLines = new();
    private readonly List<string> _verboseLogLines = new();
    private IEnableTelemetryChecker _enableTelemetryCheckerStub;
    private readonly CapturingDebugLog _debugLog = new();

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(true, "InstrumentationKey=dev", true)]
    [InlineData(false, null, true)]
    public void DebugBuildGuard_disables_the_built_in_connection_only_in_debug_builds_without_an_override(
        bool isDebugBuild, string? overrideValue, bool innerIsUsed)
    {
        const string variable = TelemetryConnectionOverride.EnvironmentVariable;
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, overrideValue);
            var inner = Substitute.For<IEnableTelemetryChecker>();
            inner.IsEnabled().Returns(true);

            var guarded = VsTelemetryTransmitter.ApplyDebugBuildGuard(inner, isDebugBuild);

            guarded.IsEnabled().Should().Be(innerIsUsed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

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
            if (m.Level == System.Diagnostics.TraceLevel.Info) _infoLogLines.Add(m.Message);
            else if (m.Level == System.Diagnostics.TraceLevel.Verbose) _verboseLogLines.Add(m.Message);
        });
        return new VsTelemetryTransmitter(telemetryClient, _enableTelemetryCheckerStub, logger, _debugLog);
    }

    // ── Unreachable endpoint (#859) ───────────────────────────────────────────────
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

        sut.TransmitEvent(new VsGenericEvent("Extension loaded"));

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
            sut.TransmitEvent(new VsGenericEvent("Extension loaded"));
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

    // ── Exception scrubbing (#1027) ───────────────────────────────────────────────

    private const string UserPath = @"C:\Users\alice\proj\x.cs";

    private static Exception ThrownWithPathInMessage()
    {
        try
        {
            throw new InvalidOperationException("Could not find file '" + UserPath + "'.",
                new System.IO.IOException("inner " + UserPath));
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Should_RedactPaths_FromTransmittedExceptionMessage()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitFatalExceptionEvent(ThrownWithPathInMessage(), isFatal: true);

        var sent = _telemetryChannel.SentTelemtries.Should().ContainSingle()
            .Which.Should().BeOfType<ExceptionTelemetry>().Subject;
        sent.ExceptionDetailsInfoList.Should().ContainSingle();
        var details = sent.ExceptionDetailsInfoList.Single();
        details.TypeName.Should().Be(typeof(InvalidOperationException).FullName);
        details.Message.Should().Be("Could not find file '<path>'.");
        sent.Message.Should().NotContain("alice");
        sent.Properties["IsFatal"].Should().Be("True");
    }

    [Fact]
    public void Should_SendOnlyTheSanitizedStack_NeverTheRawStackTrace()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();
        var exception = ThrownWithPathInMessage();

        sut.TransmitFatalExceptionEvent(exception, isFatal: true);

        // Assert on what actually goes over the wire: the raw stack (with the source file path) and the
        // inner exception never leave; only ExceptionStackSanitizer's Namespace.Type.Method[:line]
        // attribution does.
        var payload = System.Text.Encoding.UTF8.GetString(Microsoft.ApplicationInsights.Extensibility.Implementation
            .JsonSerializer.Serialize(_telemetryChannel.SentTelemtries, compress: false));
        var sanitizedStack = ExceptionStackSanitizer.Sanitize(exception);
        sanitizedStack.Should().Contain(nameof(ThrownWithPathInMessage));
        payload.Should().Contain(Newtonsoft.Json.JsonConvert.ToString(sanitizedStack).Trim('"'));
        payload.Should().NotContain("alice")
            .And.NotContain("x.cs")
            .And.NotContain("TelemetryTransmitterTests.cs")
            .And.NotContain(typeof(System.IO.IOException).FullName);
    }

    [Fact]
    public void Should_KeepRawMessage_InTheLocalDebugLogMirror()
    {
        var sut = CreateSut();
        GivenTelemetryEnabled();

        sut.TransmitFatalExceptionEvent(ThrownWithPathInMessage(), isFatal: true);

        var props = (System.Collections.IDictionary)_debugLog.Records.Single().Props;
        props["Message"].Should().Be("Could not find file '" + UserPath + "'.");
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
