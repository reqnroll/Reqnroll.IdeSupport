using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Intercepts <c>telemetry/event</c> notifications from the LSP server (Receive direction)
/// and forwards them to <see cref="ITelemetryTransmitter"/> for persistent telemetry.
/// </summary>
/// <remarks>
/// Uses a lazy <c>Func&lt;ITelemetryTransmitter?&gt;</c> (same pattern as
/// <see cref="ScaffoldTrackingInterceptor"/>) so the transmitter can be resolved
/// after the MEF composition is ready, rather than requiring it at construction time.
/// <para>
/// The server emits events (notably <c>ServerSessionStarted</c>, issue #845) immediately after the LSP
/// handshake, which can precede the transmitter's resolution by a few hundred milliseconds. Events that
/// arrive while the transmitter is unavailable are therefore held, in arrival order, and sent once it
/// resolves (<see cref="Flush"/>, or opportunistically on the next event). A held event older than
/// <see cref="HoldTtl"/> is dropped, as is the oldest one when <see cref="MaxPending"/> is exceeded, so a
/// transmitter that never resolves cannot grow memory or deliver stale events much later.
/// </para>
/// </remarks>
internal sealed class TelemetryEventInterceptor : ILspMessageInterceptor
{
    /// <summary>How long an event may wait for the transmitter to resolve before it is dropped.</summary>
    internal static readonly TimeSpan DefaultHoldTtl = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on held events.</summary>
    internal const int MaxPending = 200;

    private readonly Func<ITelemetryTransmitter?> _getTransmitter;
    private readonly ILogger<TelemetryEventInterceptor> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset ReceivedAt, ITelemetryEvent Event)> _pending = new();

    /// <summary>Maximum time an event is held waiting for the transmitter.</summary>
    internal TimeSpan HoldTtl { get; }

    /// <summary>Creates the interceptor over a deferred accessor for the (not-yet-resolved) telemetry transmitter.</summary>
    public TelemetryEventInterceptor(
        Func<ITelemetryTransmitter?> getTransmitter,
        ILogger<TelemetryEventInterceptor> logger,
        TimeSpan? holdTtl = null,
        Func<DateTimeOffset>? now = null)
    {
        _getTransmitter = getTransmitter ?? throw new ArgumentNullException(nameof(getTransmitter));
        _logger         = logger         ?? throw new ArgumentNullException(nameof(logger));
        HoldTtl         = holdTtl ?? DefaultHoldTtl;
        _now            = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sends any held, still-fresh events to the transmitter if it is now available; expired ones are
    /// dropped. Called when the transmitter is assigned so held events do not wait for the next server event.
    /// </summary>
    public void Flush()
    {
        var transmitter = _getTransmitter();
        if (transmitter is not null)
            FlushTo(transmitter);
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(
        LspMessage        message,
        CancellationToken cancellationToken)
    {
        // Only interested in notifications from the server (Receive direction).
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        if (message.Method != LspStandardMethodNames.TelemetryEvent)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        try
        {
            var eventName = message.Body["params"]?["eventName"]?.Value<string>();
            if (string.IsNullOrEmpty(eventName))
            {
                _logger.LogWarning(
                    "TelemetryEventInterceptor: telemetry/event without eventName; dropping.");
                return Task.FromResult(LspInterceptorResult.PassThrough);
            }

            var properties = new System.Collections.Generic.Dictionary<string, object>();
            var propsToken = message.Body["params"]?["properties"] as JObject;
            if (propsToken is not null)
            {
                foreach (var prop in propsToken.Properties())
                {
                    var value = prop.Value;
                    if (value is JValue jv && jv.Value is not null)
                        properties[prop.Name] = jv.Value;
                    else if (value is not null)
                        properties[prop.Name] = value.ToString();
                }
            }

            var telemetryEvent = new Reqnroll.IdeSupport.Common.Telemetry.GenericEvent(eventName!, properties);

            var transmitter = _getTransmitter();
            if (transmitter is null)
            {
                Hold(telemetryEvent);
                return Task.FromResult(LspInterceptorResult.PassThrough);
            }

            // Held events first, so the stream stays in the order the server emitted it.
            FlushTo(transmitter);
            transmitter.TransmitEvent(telemetryEvent);

            _logger.LogDebug(
                "TelemetryEventInterceptor: forwarded telemetry/event {EventName} ({PropertyCount} props)",
                eventName, properties.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TelemetryEventInterceptor: error forwarding telemetry/event.");
        }

        // Always pass through so the message continues to other interceptors and VS.
        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    private void Hold(ITelemetryEvent telemetryEvent)
    {
        var now = _now();
        lock (_gate)
        {
            DropExpired(now);
            if (_pending.Count >= MaxPending)
            {
                var dropped = _pending.Dequeue();
                _logger.LogWarning(
                    "TelemetryEventInterceptor: pending buffer full; dropping oldest event {EventName}.",
                    dropped.Event.EventName);
            }
            _pending.Enqueue((now, telemetryEvent));
        }

        _logger.LogDebug(
            "TelemetryEventInterceptor: ITelemetryTransmitter not available; holding {EventName} (ttl {Ttl}s).",
            telemetryEvent.EventName, HoldTtl.TotalSeconds);
    }

    private void FlushTo(ITelemetryTransmitter transmitter)
    {
        (DateTimeOffset ReceivedAt, ITelemetryEvent Event)[] toSend;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;
            DropExpired(_now());
            toSend = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var (_, held) in toSend)
        {
            try
            {
                transmitter.TransmitEvent(held);
                _logger.LogDebug("TelemetryEventInterceptor: forwarded held telemetry/event {EventName}.", held.EventName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TelemetryEventInterceptor: error forwarding held telemetry/event {EventName}.", held.EventName);
            }
        }
    }

    // Caller holds _gate. Events are queued in arrival order, so expired ones are always at the front.
    private void DropExpired(DateTimeOffset now)
    {
        while (_pending.Count > 0 && now - _pending.Peek().ReceivedAt > HoldTtl)
        {
            var expired = _pending.Dequeue();
            _logger.LogWarning(
                "TelemetryEventInterceptor: ITelemetryTransmitter still not available after {Ttl}s; dropping {EventName}.",
                HoldTtl.TotalSeconds, expired.Event.EventName);
        }
    }
}
