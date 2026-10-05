using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

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
    public async Task FeatureUsageSummary_counts_string_is_forwarded_verbatim()
    {
        // The server sends the per-kind counts as a compact JSON *string* (#582) precisely because a
        // nested object would be re-rendered by JToken.ToString() as multi-line indented JSON.
        var transmitter = new CapturingTransmitter();
        var sut = Create(transmitter);
        const string lookupCounts = "{\"CodeAction\":1,\"Completion.Step\":14}";

        await sut.InterceptAsync(
            Receive(TelemetryEvent("FeatureUsageSummary", new JObject { ["LookupCounts"] = lookupCounts })),
            CancellationToken.None);

        transmitter.Events.Should().ContainSingle();
        transmitter.Events[0].Properties["LookupCounts"].Should().Be(lookupCounts);
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
    public async Task A_null_transmitter_holds_the_event_without_throwing()
    {
        var sut = Create(transmitter: null);

        var act = async () => await sut.InterceptAsync(
            Receive(TelemetryEvent("event")), CancellationToken.None);

        (await act.Should().NotThrowAsync()).Which.Should().Be(LspInterceptorResult.PassThrough);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    }

    private static TelemetryEventInterceptor CreateHolding(
        Func<ITelemetryTransmitter?> getTransmitter, Clock clock, TimeSpan? ttl = null) =>
        new(getTransmitter, NullLogger<TelemetryEventInterceptor>.Instance, ttl ?? TimeSpan.FromSeconds(30), () => clock.Now);

    [Fact]
    public async Task Events_received_before_the_transmitter_resolves_are_sent_in_order_on_Flush()
    {
        ITelemetryTransmitter? transmitter = null;
        var sut = CreateHolding(() => transmitter, new Clock());

        await sut.InterceptAsync(Receive(TelemetryEvent("ServerSessionStarted")), CancellationToken.None);
        await sut.InterceptAsync(Receive(TelemetryEvent("Second")), CancellationToken.None);
        var capture = new CapturingTransmitter();
        transmitter = capture;
        capture.Events.Should().BeEmpty();

        sut.Flush();

        capture.Events.Select(e => e.EventName).Should().Equal("ServerSessionStarted", "Second");
    }

    [Fact]
    public async Task Held_events_are_sent_before_the_next_event_when_no_Flush_occurs()
    {
        ITelemetryTransmitter? transmitter = null;
        var sut = CreateHolding(() => transmitter, new Clock());
        await sut.InterceptAsync(Receive(TelemetryEvent("Held")), CancellationToken.None);
        var capture = new CapturingTransmitter();
        transmitter = capture;

        await sut.InterceptAsync(Receive(TelemetryEvent("Live")), CancellationToken.None);

        capture.Events.Select(e => e.EventName).Should().Equal("Held", "Live");
    }

    [Fact]
    public async Task Held_events_past_the_ttl_are_dropped_not_sent()
    {
        var clock = new Clock();
        ITelemetryTransmitter? transmitter = null;
        var sut = CreateHolding(() => transmitter, clock, TimeSpan.FromSeconds(30));
        await sut.InterceptAsync(Receive(TelemetryEvent("Stale")), CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(20);
        await sut.InterceptAsync(Receive(TelemetryEvent("Fresh")), CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(15); // Stale is 35s old, Fresh 15s
        var capture = new CapturingTransmitter();
        transmitter = capture;

        sut.Flush();

        capture.Events.Select(e => e.EventName).Should().Equal("Fresh");
    }

    [Fact]
    public async Task Flush_sends_each_held_event_only_once()
    {
        ITelemetryTransmitter? transmitter = null;
        var sut = CreateHolding(() => transmitter, new Clock());
        await sut.InterceptAsync(Receive(TelemetryEvent("Once")), CancellationToken.None);
        var capture = new CapturingTransmitter();
        transmitter = capture;

        sut.Flush();
        sut.Flush();

        capture.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task The_buffer_is_bounded_and_drops_the_oldest_event()
    {
        ITelemetryTransmitter? transmitter = null;
        var sut = CreateHolding(() => transmitter, new Clock());
        for (var i = 0; i < TelemetryEventInterceptor.MaxPending + 1; i++)
            await sut.InterceptAsync(Receive(TelemetryEvent($"e{i}")), CancellationToken.None);
        var capture = new CapturingTransmitter();
        transmitter = capture;

        sut.Flush();

        capture.Events.Should().HaveCount(TelemetryEventInterceptor.MaxPending);
        capture.Events[0].EventName.Should().Be("e1");
    }

    [Fact]
    public void Flush_without_a_transmitter_does_not_throw()
    {
        var sut = CreateHolding(() => null, new Clock());

        var act = () => sut.Flush();

        act.Should().NotThrow();
    }
}
