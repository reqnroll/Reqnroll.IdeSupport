#nullable enable

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Debug-only sink that mirrors telemetry events to a local file for later review.
/// <para>
/// Deliberately independent of the analytics opt-out gate (<c>REQNROLL_TELEMETRY_ENABLED</c>):
/// it records what the system <em>produced</em> even when transmission is disabled or the event
/// is dropped downstream. Implementations must never throw — telemetry debugging must not break
/// the feature path.
/// </para>
/// </summary>
public interface ITelemetryDebugLog
{
    /// <summary>Whether a sink is configured. When false, <see cref="Record"/> is a no-op.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Appends one record describing a telemetry event.
    /// </summary>
    /// <param name="source">Where the event was captured: <c>"server"</c> or <c>"host"</c>.</param>
    /// <param name="eventName">The telemetry event name.</param>
    /// <param name="properties">The event properties (serialized as a JSON object); may be null.</param>
    /// <param name="enabled">Host only: whether the opt-out gate allowed transmission.</param>
    /// <param name="transmitted">Host only: whether the event was actually handed to the channel.</param>
    /// <param name="error">Host only: the message if transmission threw, otherwise null.</param>
    void Record(
        string source,
        string eventName,
        object? properties,
        bool? enabled = null,
        bool? transmitted = null,
        string? error = null);
}
