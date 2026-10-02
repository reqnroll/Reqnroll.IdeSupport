using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Vocabulary shared by the client-originated server-lifecycle events
/// (<see cref="TelemetryEvents.ServerStartFailed"/>, <see cref="TelemetryEvents.ServerExitedUnexpectedly"/>,
/// <see cref="TelemetryEvents.ServerRestarted"/>, issue #845). A dead server cannot report itself, so each
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

    /// <summary>Builds the property set for one lifecycle event.</summary>
    public static ImmutableDictionary<string, object> BuildProperties(string reason, int attemptNumber) =>
        ImmutableDictionary<string, object>.Empty
            .Add(ReasonKey, reason)
            .Add(AttemptNumberKey, attemptNumber);
}

/// <summary>The closed set of <c>Reason</c> values for the server-lifecycle events (issue #845).</summary>
public static class ServerFailureReason
{
    /// <summary>The bundled server executable was not found on disk.</summary>
    public const string ExecutableNotFound = "ExecutableNotFound";

    /// <summary>The server failed to launch or to complete the LSP handshake (any cause other than a missing executable).</summary>
    public const string StartFailed = "StartFailed";

    /// <summary>A running server process exited without the client having asked it to.</summary>
    public const string ProcessExited = "ProcessExited";

    /// <summary>The previous server session was ended cleanly (e.g. the IDE closed a solution) and a new one was started.</summary>
    public const string SessionEnded = "SessionEnded";

    /// <summary>The user restarted a cleanly stopped server.</summary>
    public const string UserRestart = "UserRestart";
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

    /// <summary>Reports <paramref name="eventName"/> with a closed-enum <paramref name="reason"/> and the start <paramref name="attemptNumber"/>. Never throws.</summary>
    public void Report(string eventName, string reason, int attemptNumber)
    {
        var telemetryEvent = new GenericEvent(eventName, ServerLifecycleTelemetry.BuildProperties(reason, attemptNumber));
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
