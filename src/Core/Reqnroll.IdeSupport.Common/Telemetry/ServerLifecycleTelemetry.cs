using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Vocabulary shared by the client-originated server-lifecycle events
/// (<see cref="TelemetryEvents.ServerProcessStartFailed"/>, <see cref="TelemetryEvents.ServerProcessExitedUnexpectedly"/>,
/// <see cref="TelemetryEvents.ServerProcessRestarted"/>). A dead server cannot report itself, so each
/// IDE client sends these. Property names and values are mirrored in
/// <c>src/VSCode/src/telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>: keep all three in sync.
/// </summary>
public static class ServerLifecycleTelemetry
{
    /// <summary>Property key: why the event happened; always one of the <see cref="ServerFailureReason"/> values, never free text.</summary>
    public const string ReasonKey = "Reason";

    /// <summary>
    /// Property key: 1-based number of the server start attempt in this IDE session (1 = the initial
    /// start, 2 = the first restart, ...).
    /// </summary>
    public const string AttemptNumberKey = "AttemptNumber";

    /// <summary>
    /// Property key: the process exit code, as a signed integer (Windows NTSTATUS-style codes such as
    /// <c>0xE0434352</c> are negative). Only on <see cref="TelemetryEvents.ServerProcessExitedUnexpectedly"/>,
    /// and only when the process had exited by the time the client reported it.
    /// </summary>
    public const string ExitCodeKey = "ExitCode";

    /// <summary>Property key: whole milliseconds from the server process starting to it exiting (or to the report, if it had not yet exited).</summary>
    public const string UptimeMsKey = "UptimeMs";

    /// <summary>Builds the property set for one lifecycle event; <paramref name="details"/> keys are added where not already present.</summary>
    public static ImmutableDictionary<string, object> BuildProperties(
        string reason, int attemptNumber, IReadOnlyDictionary<string, object>? details = null)
    {
        var properties = ImmutableDictionary<string, object>.Empty
            .Add(ReasonKey, reason)
            .Add(AttemptNumberKey, attemptNumber);
        if (details is null)
            return properties;

        foreach (var pair in details)
        {
            if (!properties.ContainsKey(pair.Key))
                properties = properties.Add(pair.Key, pair.Value);
        }

        return properties;
    }

    /// <summary>
    /// The <see cref="ServerSession.PropertyName"/>, <see cref="ExitCodeKey"/> and <see cref="UptimeMsKey"/>
    /// details for a server process, each omitted when it cannot be read. Never throws.
    /// </summary>
    /// <param name="process">The server process, or <see langword="null"/> when it was never started.</param>
    /// <param name="serverSessionId">The id the client passed as <c>--server-session-id</c>, if any.</param>
    /// <param name="now">The current local time, used for the uptime of a process that has not exited yet.</param>
    public static IReadOnlyDictionary<string, object> DescribeProcess(Process? process, string? serverSessionId, DateTime now)
    {
        var details = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(serverSessionId))
            details[ServerSession.PropertyName] = serverSessionId!;
        if (process is null)
            return details;

        try
        {
            var exited = process.HasExited;
            if (exited)
                details[ExitCodeKey] = process.ExitCode;

            var end = exited ? process.ExitTime : now;
            var uptime = end - process.StartTime;
            if (uptime >= TimeSpan.Zero)
                details[UptimeMsKey] = (long)uptime.TotalMilliseconds;
        }
        catch (Exception)
        {
            // InvalidOperationException (no process associated, or disposed), Win32Exception (access
            // denied) and NotSupportedException (remote process): report what was read so far.
        }

        return details;
    }
}

/// <summary>The closed set of <c>Reason</c> values for the server-lifecycle events.</summary>
public static class ServerFailureReason
{
    /// <summary>The bundled server executable was not found on disk.</summary>
    public const string ExecutableNotFound = "ExecutableNotFound";

    /// <summary>The server failed to launch or to complete the LSP handshake (any cause other than a missing executable).</summary>
    public const string StartFailed = "StartFailed";

    /// <summary>A running server process exited without the client having asked it to.</summary>
    public const string ProcessExited = "ProcessExited";

    /// <summary>
    /// The previous server session ended cleanly (a normal shutdown) and a new one was started. Whether the
    /// user or the IDE itself initiated that restart (a settings change, a solution swap) is not observable
    /// from any of the three IDEs' APIs, so the two are not distinguished.
    /// </summary>
    public const string SessionEnded = "SessionEnded";
}

/// <summary>
/// Sends the client-originated server-lifecycle events through an <see cref="ITelemetryTransmitter"/> that
/// may not exist yet: the Visual Studio host resolves its transmitter lazily (after the server connection is
/// up), but a start failure happens before that. Events raised in the meantime are held (bounded) and sent
/// when <see cref="Transmitter"/> is first assigned.
/// </summary>
public sealed class ServerLifecycleReporter
{
    private const int MaxPending = 20;

    private readonly object _gate = new();
    private readonly Queue<ITelemetryEvent> _pending = new();
    private ITelemetryTransmitter? _transmitter;

    /// <summary>The transmitter to use; assigning a non-null value flushes events held while it was unavailable.</summary>
    public ITelemetryTransmitter? Transmitter
    {
        get { lock (_gate) return _transmitter; }
        set
        {
            ITelemetryEvent[] toSend;
            lock (_gate)
            {
                _transmitter = value;
                if (value is null)
                    return;
                toSend = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var e in toSend)
                TrySend(value, e);
        }
    }

    /// <summary>
    /// Reports <paramref name="eventName"/> with a closed-enum <paramref name="reason"/>, the start
    /// <paramref name="attemptNumber"/> and optional <paramref name="details"/> (see
    /// <see cref="ServerLifecycleTelemetry.DescribeProcess"/>). Never throws.
    /// </summary>
    public void Report(string eventName, string reason, int attemptNumber, IReadOnlyDictionary<string, object>? details = null)
    {
        var telemetryEvent = new GenericEvent(eventName, ServerLifecycleTelemetry.BuildProperties(reason, attemptNumber, details));
        ITelemetryTransmitter? transmitter;
        lock (_gate)
        {
            transmitter = _transmitter;
            if (transmitter is null)
            {
                if (_pending.Count >= MaxPending)
                    _pending.Dequeue();
                _pending.Enqueue(telemetryEvent);
                return;
            }
        }

        TrySend(transmitter, telemetryEvent);
    }

    private static void TrySend(ITelemetryTransmitter transmitter, ITelemetryEvent telemetryEvent)
    {
        try
        {
            transmitter.TransmitEvent(telemetryEvent);
        }
        catch (Exception)
        {
            // Best-effort telemetry must never break server startup or recovery.
        }
    }
}
