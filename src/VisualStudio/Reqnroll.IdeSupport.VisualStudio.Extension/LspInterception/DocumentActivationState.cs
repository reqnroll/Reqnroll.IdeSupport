using System;
using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Tracks, per <c>.feature</c> file path, whether a <c>reqnroll/documentActivated</c>
/// notification (issue #85) still needs to be sent for the current open-lifetime of that file.
/// Shared between <see cref="DocumentActivationTrackingInterceptor"/> (drives
/// <see cref="OnDidOpen"/>/<see cref="OnDidClose"/> from LSP traffic on the send-pump thread)
/// and a VS-side <c>WindowActivated</c> listener (drives <see cref="OnWindowActivated"/> from
/// the UI thread) — hence the plain lock rather than e.g. <c>ConcurrentDictionary</c>, since
/// each transition needs atomic "read current phase, decide the action, write new phase"
/// semantics that a lock-free dictionary API doesn't give directly.
/// </summary>
/// <remarks>
/// <para>
/// Four phases per file:
/// <list type="bullet">
/// <item><see cref="DocumentActivationPhase.NotSeen"/> — never opened, never activated.</item>
/// <item><see cref="DocumentActivationPhase.Opened"/> — <c>didOpen</c> seen, not yet notified.</item>
/// <item><see cref="DocumentActivationPhase.ActivationPending"/> — <c>WindowActivated</c> fired
/// before <c>didOpen</c> arrived (a real ordering VS can produce for a restored tab that is
/// already the active one at solution load — see #85 design discussion). Remembered so the
/// notification still fires the moment <c>didOpen</c> catches up, instead of being silently
/// treated as "already handled".</item>
/// <item><see cref="DocumentActivationPhase.Activated"/> — notification already sent for this
/// open-lifetime; every further <c>WindowActivated</c> for the same file is a pure no-op lookup
/// until the file is closed and reopened.</item>
/// </list>
/// </para>
/// <para>
/// Log data from the #78 repro (see #85 design discussion) shows VS actually sends
/// <c>didOpen</c> for every restored tab immediately at solution load, not lazily on click —
/// so <see cref="DocumentActivationPhase.ActivationPending"/> is defensive insurance for an
/// ordering that wasn't observed in practice, not the primary path this exists to fix.
/// </para>
/// </remarks>
internal enum DocumentActivationPhase
{
    NotSeen,
    Opened,
    ActivationPending,
    Activated,
}

/// <summary>What the caller of a <see cref="DocumentActivationState"/> transition should do.</summary>
internal enum DocumentActivationAction
{
    /// <summary>No LSP traffic needed — already activated, already pending, or nothing to do yet.</summary>
    None,

    /// <summary>Send <c>reqnroll/documentActivated</c> for this file now.</summary>
    SendNow,
}

/// <summary>Thread-safe per-file state machine tracking whether a document-activation notification is still owed.</summary>
internal sealed class DocumentActivationState
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DocumentActivationPhase> _phases
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A <c>.feature</c> tab became the active document.</summary>
    public DocumentActivationAction OnWindowActivated(string filePath)
    {
        lock (_lock)
        {
            var phase = GetPhase(filePath);
            switch (phase)
            {
                case DocumentActivationPhase.NotSeen:
                    _phases[filePath] = DocumentActivationPhase.ActivationPending;
                    return DocumentActivationAction.None;

                case DocumentActivationPhase.Opened:
                    _phases[filePath] = DocumentActivationPhase.Activated;
                    return DocumentActivationAction.SendNow;

                case DocumentActivationPhase.ActivationPending:
                case DocumentActivationPhase.Activated:
                default:
                    return DocumentActivationAction.None;
            }
        }
    }

    /// <summary>The server-bound pipe forwarded <c>textDocument/didOpen</c> for this file.</summary>
    public DocumentActivationAction OnDidOpen(string filePath)
    {
        lock (_lock)
        {
            var phase = GetPhase(filePath);
            switch (phase)
            {
                case DocumentActivationPhase.ActivationPending:
                    _phases[filePath] = DocumentActivationPhase.Activated;
                    return DocumentActivationAction.SendNow;

                case DocumentActivationPhase.NotSeen:
                case DocumentActivationPhase.Opened:
                case DocumentActivationPhase.Activated:
                default:
                    // Opened/Activated here means didOpen fired again without an intervening
                    // didClose (unexpected, but not this class's job to diagnose) — reset to
                    // Opened so the file gets one more activation notification rather than
                    // silently staying in a phase that can no longer produce one.
                    _phases[filePath] = DocumentActivationPhase.Opened;
                    return DocumentActivationAction.None;
            }
        }
    }

    /// <summary>The server-bound pipe forwarded <c>textDocument/didClose</c> for this file.</summary>
    public void OnDidClose(string filePath)
    {
        lock (_lock)
        {
            _phases.Remove(filePath);
        }
    }

    /// <summary>
    /// Forgets every file's phase, as when the server this state described has been replaced
    /// (issue #555).
    /// </summary>
    /// <remarks>
    /// This state is "what has this server already been told", so it only means anything relative to
    /// one server process. A relaunched server has no documents open and has received no activation
    /// notifications; carrying the old phases over would leave files stuck in
    /// <see cref="DocumentActivationPhase.Activated"/> and suppress the notification the new server
    /// still needs. The state object itself outlives the server because it is shared with the
    /// UI-thread <c>WindowActivated</c> listener, so it is cleared rather than recreated.
    /// </remarks>
    public void Reset()
    {
        lock (_lock)
        {
            _phases.Clear();
        }
    }

    private DocumentActivationPhase GetPhase(string filePath)
        => _phases.TryGetValue(filePath, out var phase) ? phase : DocumentActivationPhase.NotSeen;
}
