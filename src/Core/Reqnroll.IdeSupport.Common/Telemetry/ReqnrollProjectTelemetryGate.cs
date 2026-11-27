#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Holds a host's telemetry until the current language-server session reports that the workspace
/// contains a project that uses Reqnroll (<c>reqnroll/reqnrollProjectDetected</c>), so an IDE that
/// loads the extension for a solution without Reqnroll sends nothing but errors, server start
/// failures, and install or upgrade events.
/// </summary>
/// <remarks>
/// <para>
/// Every event the host transmits passes through <see cref="Send"/>, the ones the server relays
/// included: the server sends the notification before anything it would report about the Reqnroll
/// project, so the gate is open by the time those arrive. Errors, server start failures, install and
/// upgrade are never held (see <see cref="IsAlwaysAllowed"/>); the normal consent and opt-out checks
/// still apply to them.
/// </para>
/// <para>
/// The gate belongs to a server session, not to the IDE process: the host calls <see cref="Reset"/>
/// whenever it starts a new server process, which closes the gate again until that server reports a
/// Reqnroll project. So after a Reqnroll solution is closed and one without Reqnroll is opened, the
/// second solution's telemetry is held like the first's was. The server reports again on every start.
/// </para>
/// <para>
/// Held events are sent in order when the gate opens. At most <see cref="MaxHeldEvents"/> are kept;
/// later ones are dropped, and so is everything still held when the session ends (a reset) or the IDE
/// closes without a Reqnroll project having been seen.
/// </para>
/// </remarks>
public sealed class ReqnrollProjectTelemetryGate
{
    /// <summary>The upper bound on events held while the gate is closed.</summary>
    public const int MaxHeldEvents = 200;

    private readonly object _lock = new();
    private TaskCompletionSource<bool> _opened = NewSignal();
    private List<Action>? _held = new();

    /// <summary>The instance shared by everything in one IDE process.</summary>
    public static ReqnrollProjectTelemetryGate Shared { get; } = new();

    /// <summary><see langword="true"/> while the current server session has reported a Reqnroll project.</summary>
    public bool IsOpen
    {
        get { lock (_lock) return _held is null; }
    }

    /// <summary>Completes when the gate next opens (at once if it is open), or is cancelled through <paramref name="cancellationToken"/>.</summary>
    public async Task WaitUntilOpenAsync(CancellationToken cancellationToken)
    {
        Task opened;
        lock (_lock)
            opened = _opened.Task;

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
        {
            var completed = await Task.WhenAny(opened, cancelled.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see langword="true"/> for events sent even before a Reqnroll project is seen: errors
    /// (<c>Error.*</c>), the extension being installed or upgraded, and the server failing to start
    /// or exiting unexpectedly.
    /// </summary>
    public static bool IsAlwaysAllowed(string? eventName) =>
        (eventName is not null && eventName.StartsWith("Error.", StringComparison.Ordinal)) ||
        eventName == TelemetryEvents.LifecycleExtensionInstalled ||
        eventName == TelemetryEvents.LifecycleExtensionUpgraded ||
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

    /// <summary>Opens the gate and sends the held events in order. Does nothing while already open.</summary>
    public void Open()
    {
        List<Action>? held;
        TaskCompletionSource<bool> opened;
        lock (_lock)
        {
            held = _held;
            _held = null;
            opened = _opened;
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

        opened.TrySetResult(true);
    }

    /// <summary>
    /// Closes the gate for a new server session: later events are held until that server reports a
    /// Reqnroll project. Events still held from the previous session are dropped, since that session
    /// never reported one. A wait that has not completed yet keeps waiting for the next open.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_held is null)
                _held = new List<Action>();
            else
                _held.Clear();

            if (_opened.Task.IsCompleted)
                _opened = NewSignal();
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
