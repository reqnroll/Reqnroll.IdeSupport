using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Owns the request/response correlation for requests <em>we</em> inject into the server — the
/// <c>reqnroll-rpc-</c> id space and the waiters attached to it (issue #587, step 2).
/// </summary>
/// <remarks>
/// <para>
/// Injected requests use a string id with <see cref="RequestIdPrefix"/> so they can never collide
/// with VS's own JSON-RPC ids, which are always plain integers. The receive pump recognises the
/// prefix and consumes the response before it can be forwarded to VS — which never sent the request
/// and would treat the response as a fatal protocol violation.
/// </para>
/// <para>
/// Deliberately free of I/O and pipe state: the whole point of extracting it is that the failure
/// shapes behind #395 and #401 become testable without standing up a connection.
/// </para>
/// </remarks>
internal sealed class LspRequestCorrelator
{
    /// <summary>Prefix identifying an id this correlator issued. Only <see cref="Begin"/> ever produces one.</summary>
    public const string RequestIdPrefix = "reqnroll-rpc-";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken?>> _pendingRequests
        = new ConcurrentDictionary<string, TaskCompletionSource<JToken?>>(StringComparer.Ordinal);

    private readonly ILogger _logger;

    /// <summary>Creates the correlator over the given logging sink.</summary>
    public LspRequestCorrelator(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a new owned request and returns its id plus the task that completes when the
    /// response is consumed, the connection is released, or <paramref name="cancellationToken"/>
    /// fires. Dispose the returned <see cref="PendingRequest"/> once the caller is done with it.
    /// </summary>
    /// <remarks>
    /// Cancellation is registered before the caller sends anything, avoiding the race where the
    /// token is already cancelled at the point registration would otherwise have happened.
    /// </remarks>
    public PendingRequest Begin(CancellationToken cancellationToken)
    {
        var id  = RequestIdPrefix + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return new PendingRequest(this, id, tcs.Task, registration);
    }

    /// <summary>
    /// True if <paramref name="body"/> is a response to a request this correlator issued, identified
    /// purely by <see cref="RequestIdPrefix"/>. Pure — see <see cref="Consume"/> for actually
    /// consuming it.
    /// </summary>
    /// <remarks>
    /// Static, and deliberately independent of the pending map: the prefix alone proves the response
    /// is ours, which is exactly what issue #401 turns on. Split from <see cref="Consume"/> so the
    /// caller can run the response through the receive interceptors in between recognising and
    /// consuming it (issue #491) — that is how <see cref="LspInspectorLogger"/> sees owned-RPC
    /// traffic that never reaches VS.
    /// </remarks>
    public static bool IsOwnedResponse(JObject body, out string id)
    {
        id = string.Empty;

        // A JSON-RPC response has an "id" and either "result" or "error", but no "method".
        if (body.ContainsKey("method")) return false;

        var idValue = body["id"]?.Value<string>();
        if (idValue is null || !idValue.StartsWith(RequestIdPrefix, StringComparison.Ordinal)) return false;

        id = idValue;
        return true;
    }

    /// <summary>
    /// Consumes an owned response, completing its waiter when one is still registered.
    /// </summary>
    /// <remarks>
    /// Issue #401: a response must never be forwarded to VS just because the caller already gave up
    /// on it. <see cref="PendingRequest.Dispose"/> removes the id as soon as the caller's token
    /// fires — e.g. a <c>StepCodeLensService</c> request cancelled mid-reconnect — which can race the
    /// server's real response arriving a few milliseconds later. Previously that race let the
    /// response fall through and hand VS's JsonRpc a response to a request it never sent: the same
    /// <c>RemoteProtocolViolation</c> #395 fixed for VS's own peer-session responses, via this side
    /// channel instead. Since the id prefix alone proves the response is ours, it is always safe
    /// (and correct) to consume it here whether or not a waiter survives.
    /// </remarks>
    public void Consume(string id, JObject body)
    {
        if (_pendingRequests.TryRemove(id, out var tcs))
        {
            if (body.ContainsKey("error"))
                tcs.TrySetResult(null);
            else
                tcs.TrySetResult(body["result"]);

            _logger.LogInformation("LspRequestCorrelator: consumed correlated response id={Id}", id);
        }
        else
        {
            _logger.LogInformation(
                "LspRequestCorrelator: dropped response id={Id} — no pending request (already " +
                "cancelled/removed), but the {Prefix} id proves it's ours; forwarding it to VS " +
                "would be an unmatched response and fatally close the connection (issue #401).",
                id, RequestIdPrefix);
        }
    }

    /// <summary>
    /// Releases every waiter with a <see langword="null"/> result — the server behind this
    /// connection is gone and no response can ever arrive (issue #555).
    /// </summary>
    /// <remarks>
    /// Without this they sit until each caller's own <see cref="CancellationToken"/> trips, which is
    /// what turned that failure into a stream of <see cref="OperationCanceledException"/>s from every
    /// CodeLens and navigation-bar request for the rest of the session, rather than a prompt
    /// "there is no server".
    /// </remarks>
    public void ReleaseAll()
    {
        foreach (var kv in _pendingRequests)
            kv.Value.TrySetResult(null);
        _pendingRequests.Clear();
    }

    /// <summary>Cancels every waiter, so no caller hangs past disposal.</summary>
    public void CancelAll()
    {
        foreach (var kv in _pendingRequests)
            kv.Value.TrySetCanceled();
        _pendingRequests.Clear();
    }

    /// <summary>One in-flight owned request: its id, the task awaiting its response, and the cleanup for both.</summary>
    public sealed class PendingRequest : IDisposable
    {
        private readonly LspRequestCorrelator _owner;
        private readonly CancellationTokenRegistration _registration;

        internal PendingRequest(
            LspRequestCorrelator owner, string id, Task<JToken?> response, CancellationTokenRegistration registration)
        {
            _owner        = owner;
            _registration = registration;
            Id            = id;
            Response      = response;
        }

        /// <summary>The generated <see cref="RequestIdPrefix"/> id to put on the wire.</summary>
        public string Id { get; }

        /// <summary>Completes with the response's <c>result</c>, or null on error/release/cancellation.</summary>
        public Task<JToken?> Response { get; }

        /// <summary>Unregisters the waiter. A response arriving afterwards is still consumed, never forwarded (issue #401).</summary>
        public void Dispose()
        {
            _registration.Dispose();
            _owner._pendingRequests.TryRemove(Id, out _);
        }
    }
}
