using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="TelemetryEventInterceptor"/> forwards server <c>telemetry/event</c> notifications
/// to the analytics transmitter and otherwise leaves the message stream untouched.
/// </summary>
public class TelemetryEventInterceptorTests
{
    private sealed class CapturingTransmitter : ITelemetryTransmitter
    {
        public List<ITelemetryEvent> Events { get; } = new();
        public void TransmitEvent(ITelemetryEvent runtimeEvent) => Events.Add(runtimeEvent);
        public void TransmitExceptionEvent(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps) { }
        public void TransmitFatalExceptionEvent(Exception exception, bool isFatal) { }
    }

    private static TelemetryEventInterceptor Create(ITelemetryTransmitter? transmitter) =>
        new(() => transmitter, NullLogger<TelemetryEventInterceptor>.Instance);

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);
    private static LspMessage Send(JObject body)    => new(LspMessageDirection.Send,    body, DateTimeOffset.Now);

    private static JObject TelemetryEvent(string? eventName, JObject? properties = null)
    {
        var paramsObj = new JObject();
        if (eventName is not null) paramsObj["eventName"] = eventName;
        if (properties is not null) paramsObj["properties"] = properties;
        return new JObject { ["jsonrpc"] = "2.0", ["method"] = "telemetry/event", ["params"] = paramsObj };
    }

    [Fact]
    public async Task A_telemetry_event_is_forwarded_with_name_and_properties()
    {
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);

        var result = await sut.InterceptAsync(
            Receive(TelemetryEvent("GoToStepDefinition command executed", new JObject
            {
                ["GenerateSnippet"] = true,
                ["Count"] = 3,
                ["Source"] = "codeLens",
            })),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        transmitter.Events.Should().ContainSingle();
        var ev = transmitter.Events[0];
        ev.EventName.Should().Be("GoToStepDefinition command executed");
        ev.Properties.Should().ContainKey("GenerateSnippet");
        ev.Properties["Source"].Should().Be("codeLens");
        ev.Properties["Count"].Should().Be(3L); // JSON integers surface as Int64
    }

    [Fact]
    public async Task A_telemetry_event_without_properties_forwards_an_empty_property_bag()
    {
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);

        await sut.InterceptAsync(Receive(TelemetryEvent("Some event")), CancellationToken.None);

        transmitter.Events.Should().ContainSingle();
        transmitter.Events[0].Properties.Should().BeEmpty();
    }

    [Fact]
    public async Task A_telemetry_event_without_an_event_name_is_dropped()
    {
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);

        var result = await sut.InterceptAsync(Receive(TelemetryEvent(eventName: null)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        transmitter.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_telemetry_event_sent_to_the_server_is_ignored()
    {
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);

        var result = await sut.InterceptAsync(Send(TelemetryEvent("client side")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        transmitter.Events.Should().BeEmpty("only server→client (Receive) telemetry is forwarded");
    }

    [Fact]
    public async Task A_non_telemetry_message_passes_through_without_forwarding()
    {
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);

        var result = await sut.InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["method"] = "window/logMessage" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        transmitter.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_transmitter_drops_the_event_without_throwing()
    {
        var sut = Create(transmitter: null);

        var act = async () => await sut.InterceptAsync(
            Receive(TelemetryEvent("event")), CancellationToken.None);

        (await act.Should().NotThrowAsync()).Which.Should().Be(LspInterceptorResult.PassThrough);
    }
}
