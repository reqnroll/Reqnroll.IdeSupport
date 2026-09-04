using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>Where a server → VS response should go, once its owning session is known.</summary>
internal enum ResponseRouting
{
    /// <summary>Not a response at all (a request or notification from the server) — routing does not apply.</summary>
    NotAResponse,

    /// <summary>A response for the session currently in effect: forward it to VS.</summary>
    DeliverToCurrentSession,

    /// <summary>A response for a session that has since been abandoned: drop it (issue #395).</summary>
    DropAbandoned
}

/// <summary>
/// Tracks which VS-facing session sent each of VS's own outstanding requests, so a response that
/// lands after that session was abandoned can be recognised and dropped (issue #587 step 2,
/// extracted from <see cref="LspInterceptingPipe"/>).
/// </summary>
/// <remarks>
/// <para>
/// Issue #395: without this, a response arriving after a session swap gets forwarded — via the
/// receive pump's "whichever pipe is current" policy, correct for server-pushed notifications and
/// requests but wrong here — to the <em>new</em> session's JsonRpc, which never sent the matching
/// request. VS's JsonRpc treats an unmatched response as a fatal protocol violation and closes the
/// brand-new connection outright: confirmed via a captured repro, where <c>id=143</c>'s
/// <c>shutdown</c> response from the abandoned session arrived 71ms after the swap and was
/// misdelivered, whose trace shows "RemoteProtocolViolation: A response was received without a
/// request having been sent" followed immediately by "Connection closing".
/// </para>
/// <para>
/// A response whose request belongs to an older, already-abandoned session is simply dropped —
/// nothing is listening on that old session's pipe any more either, so there is no correct
/// destination to route it to.
/// </para>
/// <para>
/// The current session id is passed in per call rather than held here, so this type stays a pure
/// function of what it is told and #395's shape can be tested without a pipe.
/// </para>
/// </remarks>
internal sealed class VsSessionRouter
{
    private readonly ConcurrentDictionary<string, int> _requestSessionsById = new(StringComparer.Ordinal);

    /// <summary>Records that <paramref name="sessionId"/> sent the request <paramref name="id"/>, before it is forwarded.</summary>
    public void RecordOutboundRequest(string id, int sessionId) => _requestSessionsById[id] = sessionId;

    /// <summary>
    /// Decides where <paramref name="body"/> should go. Consumes the tracked entry when the message
    /// is a response, since a response arrives at most once.
    /// </summary>
    /// <param name="body">The parsed server → VS message.</param>
    /// <param name="currentSessionId">The VS-facing session in effect right now.</param>
    /// <param name="owningSessionId">The session that sent the matching request, when one is tracked; otherwise 0.</param>
    public ResponseRouting Route(JObject body, int currentSessionId, out int owningSessionId)
    {
        owningSessionId = 0;

        if (!LspJsonRpc.TryGetResponseId(body, out var responseId))
            return ResponseRouting.NotAResponse;

        if (!_requestSessionsById.TryRemove(responseId, out owningSessionId))
            return ResponseRouting.DeliverToCurrentSession;

        return owningSessionId == currentSessionId
            ? ResponseRouting.DeliverToCurrentSession
            : ResponseRouting.DropAbandoned;
    }

    /// <summary>Removes tracked request → session entries older than <paramref name="minimumLiveSessionId"/> (issue #395).</summary>
    /// <remarks>
    /// Bounds growth: a request still in flight when its session is abandoned, and which never
    /// receives a response, would otherwise leak its entry forever.
    /// </remarks>
    public void PurgeOlderThan(int minimumLiveSessionId)
    {
        foreach (var kvp in _requestSessionsById)
        {
            if (kvp.Value < minimumLiveSessionId)
                _requestSessionsById.TryRemove(kvp.Key, out _);
        }
    }
}
