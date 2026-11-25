#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Holds a host's telemetry until the language server reports that the workspace contains a
/// project that uses Reqnroll (<c>reqnroll/reqnrollProjectDetected</c>), so an IDE that loads the
/// extension for a solution without Reqnroll sends nothing but errors.
/// </summary>
/// <remarks>
/// <para>
/// Every event the host transmits passes through <see cref="Send"/>, the ones the server relays
/// included: the server sends the notification before anything it would report about the Reqnroll
/// project, so the gate is open by the time those arrive. Errors and server start failures are
/// never held (see <see cref="IsAlwaysAllowed"/>): they are worth knowing about whatever the
/// solution contains, and the normal consent and opt-out checks still apply to them.
/// </para>
/// <para>
/// Held events are sent in order when the gate opens. At most <see cref="MaxHeldEvents"/> are kept;
/// later ones are dropped, and so is everything still held when the IDE closes without a Reqnroll
/// project having been seen. Once open, the gate stays open for the rest of the IDE process.
/// </para>
/// </remarks>
public sealed class ReqnrollProjectTelemetryGate
{
    /// <summary>The upper bound on events held while the gate is closed.</summary>
    public const int MaxHeldEvents = 200;

    private readonly object _lock = new();
    private readonly TaskCompletionSource<bool> _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<Action>? _held = new();

    /// <summary>The instance shared by everything in one IDE process.</summary>
    public static ReqnrollProjectTelemetryGate Shared { get; } = new();

    /// <summary><see langword="true"/> once a Reqnroll project has been reported.</summary>
    public bool IsOpen
    {
        get { lock (_lock) return _held is null; }
    }

    /// <summary>Completes when the gate opens, or is cancelled through <paramref name="cancellationToken"/>.</summary>
    public async Task WaitUntilOpenAsync(CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
        {
            var completed = await Task.WhenAny(_opened.Task, cancelled.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see langword="true"/> for events sent even before a Reqnroll project is seen: errors
    /// (<c>Error.*</c>) and the server failing to start or exiting unexpectedly.
    /// </summary>
    public static bool IsAlwaysAllowed(string? eventName) =>
        (eventName is not null && eventName.StartsWith("Error.", StringComparison.Ordinal)) ||
        eventName == TelemetryEvents.ServerProcessStartFailed ||
        eventName == TelemetryEvents.ServerProcessExitedUnexpectedly ||
        eventName == TelemetryEvents.ServerProcessConnectionLost;

    /// <summary>
    /// Runs <paramref name="send"/> now if the gate is open or the event is always allowed,
    /// otherwise holds it until <see cref="Open"/>.
    /// </summary>
    public void Send(string? eventName, Action send)
    {
        if (!IsAlwaysAllowed(eventName))
        {
            lock (_lock)
            {
                if (_held is not null)
                {
                    if (_held.Count < MaxHeldEvents)
                        _held.Add(send);
                    return;
                }
            }
        }

        send();
    }

    /// <summary>Opens the gate and sends the held events in order. Later calls do nothing.</summary>
    public void Open()
    {
        List<Action>? held;
        lock (_lock)
        {
            held = _held;
            _held = null;
        }

        if (held is null)
            return;

        foreach (var send in held)
        {
            try
            {
                send();
            }
            catch
            {
                // A transmitter failure on one held event must not stop the others.
            }
        }

        _opened.TrySetResult(true);
    }
}
