using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Inserts an interception pipeline between VS and the LSP server process.
/// </summary>
/// <remarks>
/// <para>
/// Sits in the middle of the LSP stdio channel:
/// <code>
///   VS  ──write──► [VS-facing PipeReader/Writer]
///                        │                  ▲
///                  SendPump task      ReceivePump task
///                        │                  │
///                        ▼                  │
///              Server stdin PipeWriter   Server stdout PipeReader
/// </code>
/// </para>
/// <para>
/// Each pump reads raw LSP frames (<c>Content-Length: N\r\n\r\nBODY</c>), parses them to
/// <see cref="LspMessage"/>, runs the relevant interceptor list, then — if no interceptor
/// consumed the message — re-encodes and forwards.
/// </para>
/// <para>
/// <b>Issue #156:</b> VS.Extensibility can call <c>ReqnrollLanguageClient.CreateServerConnectionAsync</c>
/// more than once in a session — decompiling VS's own LSP client host confirmed this is by design
/// (extension hot-reload support), not a bug on VS's side, and every Microsoft sample builds a fresh
/// <c>IDuplexPipe</c> per call rather than caching one. This type now supports that: the single,
/// persistent <see cref="_serverPipe"/> connection to the actual server process never changes, but
/// the local, in-memory "VS-facing" <see cref="Pipe"/> pair is recreated on every
/// <see cref="CreateFreshVsFacingPipe"/> call. The always-running receive pump (server → VS
/// direction) looks up the *current* VS-facing writer per frame rather than capturing one for its
/// whole lifetime, and a fresh, session-scoped send pump (VS → server direction) is started for each
/// new VS-facing pipe, replacing (and cancelling) the previous one. See that method's remarks for
/// the abandoned-session cleanup.
/// </para>
/// </remarks>
internal sealed class LspInterceptingPipe : IDisposable
{
    private readonly IDuplexPipe                       _serverPipe;
    private readonly IReadOnlyList<ILspMessageInterceptor> _sendInterceptors;
    private readonly IReadOnlyList<ILspMessageInterceptor> _receiveInterceptors;
    private readonly ILogger<LspInterceptingPipe>       _logger;

    // The two Pipe objects whose Reader/Writer ends form the *current* VS-facing IDuplexPipe.
    // VS reads from _toVsPipe.Reader; VS writes to _fromVsPipe.Writer. Replaced wholesale by
    // CreateFreshVsFacingPipe on every CreateServerConnectionAsync call (issue #156) -- guarded by
    // _vsPipeSwapLock since the persistent receive pump reads the current _toVsPipe reference
    // concurrently with swaps happening on VS's calling thread.
    private Pipe _toVsPipe   = new Pipe();   // server → VS direction
    private Pipe _fromVsPipe = new Pipe();   // VS → server direction
    private readonly object _vsPipeSwapLock = new object();

    // Serialises injected writes against the send pump so frames are not interleaved.
    private readonly SemaphoreSlim _injectLock = new SemaphoreSlim(1, 1);

    // ── Owned request/response correlation ─────────────────────────────────
    // Requests injected by us use a string id with this prefix so they never collide
    // with VS's own numeric JSON-RPC ids.  The receive pump recognises the prefix and
    // consumes the response before it can be forwarded to VS (which never sent the request).
    private const string RequestIdPrefix = "reqnroll-rpc-";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken?>> _pendingRequests
        = new ConcurrentDictionary<string, TaskCompletionSource<JToken?>>();

    // ── Peer-session response routing (issue #395) ──────────────────────────
    // Tracks which VS-facing session sent each of VS's own outstanding requests (e.g. `shutdown`,
    // sent by the old session as its last act before CreateFreshVsFacingPipe abandons it). Without
    // this, a response that lands after the swap gets forwarded — via GetCurrentToVsWriter's
    // "whichever pipe is current" policy, correct for server-pushed notifications/requests but
    // wrong here — to the *new* session's JsonRpc, which never sent the matching request. VS's
    // JsonRpc treats an unmatched response as a fatal protocol violation and closes the brand-new
    // connection outright: confirmed via a captured repro, `id=143`'s "shutdown" response from the
    // abandoned session arrived 71ms after the swap and was misdelivered to the new session, whose
    // trace shows "RemoteProtocolViolation: A response was received without a request having been
    // sent" followed immediately by "Connection closing". A response whose request belongs to an
    // older, already-abandoned session is simply dropped here — nothing is listening on that old
    // session's pipe anymore either, so there is no correct destination to route it to.
    private readonly ConcurrentDictionary<string, int> _requestSessionsById = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationTokenSource? _linkedCts;
    private CancellationToken        _lifetimeToken;
    private Task?                    _receivePump;

    // The current VS-facing session's send pump + its own cancellation, replaced on every
    // CreateFreshVsFacingPipe call. Guarded by _vsPipeSwapLock alongside the Pipe fields above.
    private CancellationTokenSource? _currentSendPumpCts;
    private Task?                    _currentSendPump;
    private int                      _sessionCounter;

    private bool _disposed;

    // ── Session termination (issue #555) ────────────────────────────────────
    // Set once this connection's server has been told to terminate (VS's own `exit`) or is known to
    // have gone. Everything downstream is then pointless: the process is on its way out, so injected
    // traffic can only hang until its caller's token trips.
    private volatile bool _serverTerminated;

    /// <summary>
    /// <see langword="true"/> once the server behind this pipe has been asked to <c>exit</c> (or is
    /// otherwise known to be gone), meaning this connection can never serve another request.
    /// </summary>
    /// <remarks>
    /// Issue #555: VS ends an LSP session by sending <c>shutdown</c> then <c>exit</c> — which it does
    /// on a solution close, not only at IDE shutdown. Those go through to the real server, which
    /// obeys and terminates. <see cref="LspServerConnectionService"/> reads this to know the
    /// connection is spent and a fresh server must be launched for the next session.
    /// </remarks>
    public bool ServerTerminated => _serverTerminated;

    /// <summary>
    /// Initialises the intercepting pipe but does not start pumping yet.
    /// Call <see cref="StartAsync"/> to begin.
    /// </summary>
    /// <param name="serverPipe">
    /// The raw <see cref="IDuplexPipe"/> connected to the server process's stdio.
    /// </param>
    /// <param name="sendInterceptors">
    /// Interceptors applied to messages travelling VS → Server.
    /// </param>
    /// <param name="receiveInterceptors">
    /// Interceptors applied to messages travelling Server → VS.
    /// </param>
    /// <param name="logger">Logging sink for pump-level diagnostics.</param>
    public LspInterceptingPipe(
        IDuplexPipe serverPipe,
        IReadOnlyList<ILspMessageInterceptor> sendInterceptors,
        IReadOnlyList<ILspMessageInterceptor> receiveInterceptors,
        ILogger<LspInterceptingPipe> logger)
    {
        _serverPipe          = serverPipe          ?? throw new ArgumentNullException(nameof(serverPipe));
        _sendInterceptors    = sendInterceptors    ?? throw new ArgumentNullException(nameof(sendInterceptors));
        _receiveInterceptors = receiveInterceptors ?? throw new ArgumentNullException(nameof(receiveInterceptors));
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the persistent receive pump (server → VS direction, bound to the real server
    /// process's stdout for the lifetime of this instance). Returns immediately; runs until the
    /// connection closes or <see cref="Dispose"/> is called. Does <b>not</b> start a send pump or
    /// hand back a VS-facing pipe — call <see cref="CreateFreshVsFacingPipe"/> for that, once per
    /// <c>CreateServerConnectionAsync</c> call.
    /// </summary>
    public Task StartAsync(CancellationToken externalCancellation)
    {
        _linkedCts     = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCancellation);
        _lifetimeToken = _linkedCts.Token;

        // Server stdout → ReceivePump reads _serverPipe.Input → current _toVsPipe.Writer → VS
        // reads that pipe's Reader. The destination is looked up per frame (GetCurrentToVsWriter)
        // rather than captured once, since CreateFreshVsFacingPipe can swap it out from under this
        // long-running pump at any time.
        _receivePump = ReceivePumpAsync(_lifetimeToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a brand-new local VS-facing <see cref="Pipe"/> pair and returns the
    /// <see cref="IDuplexPipe"/> to hand to VS as the <c>CreateServerConnectionAsync</c> result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #156: VS can call <c>CreateServerConnectionAsync</c> more than once per session. The
    /// previous implementation cached and returned the exact same <c>IDuplexPipe</c> every time,
    /// which is fine if VS never calls twice but corrupts the connection the moment it does — a
    /// second consumer writing to a <see cref="PipeWriter"/> the first consumer's disposal already
    /// completed throws <c>InvalidOperationException: Writing is not allowed after writer was
    /// completed</c>, exactly what was observed. This method instead gives every call a fresh,
    /// never-before-used pipe pair, matching every Microsoft sample's
    /// <c>CreateServerConnectionAsync</c> implementation (each builds a fresh
    /// <c>FullDuplexStream.CreatePair()</c> inline, never caches).
    /// </para>
    /// <para>
    /// The real server process connection (<see cref="_serverPipe"/>) is untouched by this — only
    /// the local, in-memory relay pipes change. The previous session's send pump (VS → server) is
    /// cancelled; its abandoned <c>_toVsPipe.Writer</c> (server → VS; ours to complete) is completed
    /// so a lingering VS-side reader gets a clean EOF instead of an error. We don't touch the
    /// abandoned <c>_fromVsPipe.Writer</c> (VS → server) since VS owns that end, not us.
    /// </para>
    /// </remarks>
    public IDuplexPipe CreateFreshVsFacingPipe()
    {
        var newToVsPipe   = new Pipe();
        var newFromVsPipe = new Pipe();
        var newSendPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);

        Pipe?                     oldToVsPipe;
        CancellationTokenSource?  oldSendPumpCts;
        int                       sessionId;

        lock (_vsPipeSwapLock)
        {
            oldToVsPipe    = _toVsPipe;
            oldSendPumpCts = _currentSendPumpCts;

            _toVsPipe            = newToVsPipe;
            _fromVsPipe          = newFromVsPipe;
            _currentSendPumpCts  = newSendPumpCts;
            sessionId            = ++_sessionCounter;
        }

        // Abandon the previous session: stop its send pump and let any lingering VS-side reader
        // of the old server→VS pipe see a clean end-of-stream rather than an error.
        oldSendPumpCts?.Cancel();
        oldSendPumpCts?.Dispose();
        try
        {
            oldToVsPipe?.Writer.Complete();
        }
        catch (Exception ex)
        {
            // Benign: e.g. already completed by a prior call, or a race with an in-flight
            // ReceivePumpAsync write to the pipe we're abandoning right now (see that method's
            // remarks on tolerating a stale-destination write failure).
            _logger.LogDebug(ex, "LspInterceptingPipe: completing the abandoned server→VS pipe threw (benign).");
        }

        _logger.LogInformation(
            "LspInterceptingPipe: CreateFreshVsFacingPipe — session #{SessionId} (issue #156: no longer " +
            "handing back a cached, possibly-dead pipe on repeat CreateServerConnectionAsync calls).",
            sessionId);

        // Bound _requestSessionsById's growth (issue #395): a request that's still in flight when
        // its session gets abandoned and never receives a response (e.g. genuinely dropped, not
        // just delayed) would otherwise leak its entry forever. Two generations is enough slack for
        // the straggler this dictionary exists to catch — a response landing shortly after its own
        // session was abandoned (like the shutdown response that motivated this fix) — without
        // holding entries from sessions abandoned long ago.
        PurgeStaleRequestSessions(minimumLiveSessionId: sessionId - 1);

        // VS → server direction for this session only. lockDestination: true -- this pump's
        // destination (_serverPipe.Output) is the same stream SendNotificationToServerAsync/
        // SendRequestToServerAsync inject into from other threads.
        _currentSendPump = SendPumpAsync(newFromVsPipe.Reader, sessionId, newSendPumpCts.Token);

        return new DuplexPipeAdapter(newToVsPipe.Reader, newFromVsPipe.Writer);
    }

    /// <summary>
    /// Records that the server behind this pipe is terminating, and releases everything still
    /// waiting on it (issue #555).
    /// </summary>
    /// <param name="reason">Why the server is considered gone; logged.</param>
    /// <remarks>
    /// Idempotent, and safe to call from any thread or from
    /// <see cref="LspServerConnectionService"/> when it notices the process has exited by some other
    /// route (a crash, or being killed). Faulting the pending injected requests here is the point:
    /// without it they sit until each caller's own <see cref="CancellationToken"/> trips, which is
    /// what turned this failure into a stream of <c>OperationCanceledException</c>s from every
    /// CodeLens and navigation-bar request for the rest of the session, rather than a prompt
    /// "there is no server".
    /// </remarks>
    public void MarkServerTerminated(string reason)
    {
        if (_serverTerminated) return;
        _serverTerminated = true;

        _logger.LogInformation(
            "LspInterceptingPipe: server considered terminated — {Reason}. This connection is spent; " +
            "further injected traffic is refused and a new server must be launched for the next " +
            "session (issue #555).", reason);

        foreach (var kv in _pendingRequests)
            kv.Value.TrySetResult(null);
        _pendingRequests.Clear();
    }

    /// <summary>
    /// True if <paramref name="body"/> is the LSP <c>exit</c> notification — a notification (no
    /// <c>id</c>) whose method is <c>exit</c>. Per the spec this asks the server to terminate its
    /// process, so it is the definitive end-of-session marker on the VS → server direction.
    /// </summary>
    /// <remarks>
    /// An <c>id</c> present but JSON-null counts as absent: <c>JObject["id"]</c> returns a
    /// <see cref="JTokenType.Null"/> token for <c>"id":null</c>, not a C# <see langword="null"/>, so
    /// testing for the latter alone would miss such a frame and leave the connection looking alive
    /// after its server had been told to leave — the whole failure this detection exists to prevent.
    /// </remarks>
    private static bool IsExitNotification(JObject body) =>
        (body["id"] is null || body["id"]!.Type == JTokenType.Null) &&
        string.Equals(body["method"]?.Value<string>(), "exit", StringComparison.Ordinal);

    /// <summary>Removes tracked request→session entries older than <paramref name="minimumLiveSessionId"/> (issue #395).</summary>
    private void PurgeStaleRequestSessions(int minimumLiveSessionId)
    {
        foreach (var kvp in _requestSessionsById)
        {
            if (kvp.Value < minimumLiveSessionId)
                _requestSessionsById.TryRemove(kvp.Key, out _);
        }
    }

    private PipeWriter GetCurrentToVsWriter()
    {
        lock (_vsPipeSwapLock)
        {
            return _toVsPipe.Writer;
        }
    }

    /// <summary>The session id of the VS-facing session currently in effect (see <see cref="_sessionCounter"/>).</summary>
    private int GetCurrentSessionId()
    {
        lock (_vsPipeSwapLock)
        {
            return _sessionCounter;
        }
    }

    // ── Pump loops ───────────────────────────────────────────────────────────

    /// <summary>
    /// Persistent, single-instance pump for the server → VS direction: reads
    /// <see cref="_serverPipe"/>'s real stdout for the lifetime of this object, forwarding each
    /// frame to whichever VS-facing pipe is <i>current</i> at that moment (see
    /// <see cref="GetCurrentToVsWriter"/>). Must never exit while the server process is alive --
    /// unlike <see cref="SendPumpAsync"/>, a failure here would silently stop relaying server output
    /// to every future VS session, not just the current one. A write failure against a
    /// possibly-stale destination (e.g. a race with <see cref="CreateFreshVsFacingPipe"/> completing
    /// the pipe this frame was about to be written to) is therefore logged and tolerated rather than
    /// treated as pump-ending.
    /// </summary>
    private async Task ReceivePumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await LspFrameCodec.ReadNextFrameAsync(_serverPipe.Input, ct).ConfigureAwait(false);
                if (frame is null)
                    break; // server process ended its stdout -- genuinely fatal, nothing more to relay.

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await ForwardToCurrentVsWriterAsync(frame.RawBytes, ct).ConfigureAwait(false);
                    continue;
                }

                // OmniSharp's DocumentUri unconditionally lowercases drive letters, but VS
                // tracks documents using the project system's original (upper-case) casing.
                // Normalize here — before correlation and before any interceptor sees the
                // message — so every server→VS path (owned-RPC responses consumed below,
                // and messages forwarded on to VS's own LSP client) gets a VS-matching URI.
                // Guarded like an interceptor (see RunInterceptorsAsync): a bug here must
                // degrade to "URI casing unfixed for this message", never sever the pipe —
                // and unlike an interceptor fault, this runs before LspInspectorLogger sees
                // the message, so a silent failure here would leave no trace in the wire log.
                var rawBytes = frame.RawBytes;
                try
                {
                    if (DriveLetterUriNormalizer.NormalizeInPlace(body))
                        rawBytes = LspFrameCodec.EncodeFrame(body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "LspInterceptingPipe: DriveLetterUriNormalizer threw on message {Body}",
                        body.ToString());
                }

                // Consume correlated responses before *forwarding* to VS (they must never reach
                // VS's own JsonRpc, which never sent them) — but still run them through
                // _receiveInterceptors first (issue #491) so LspInspectorLogger sees this traffic
                // like everything else on the pipe. Without this, every owned-RPC response
                // (e.g. reqnroll/resolveTestTargets) is invisible to the inspector log even though
                // it genuinely crossed the wire, which made a real N+1 request-storm bug look like
                // near-total silence when first diagnosed.
                if (TryGetCorrelatedResponseId(body, out var correlatedId))
                {
                    var correlatedMessage = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                    await RunInterceptorsAsync(correlatedMessage, _receiveInterceptors, ct).ConfigureAwait(false);

                    CompleteCorrelatedResponse(correlatedId, body);
                    continue;
                }

                // Drop a response whose matching request was sent by a since-abandoned VS-facing
                // session (issue #395) — forwarding it to whichever session is current would hand
                // an unmatched response to a JsonRpc instance that never sent that request, which
                // VS treats as a fatal protocol violation and closes the brand-new connection over.
                // Nothing is listening on the abandoned session's own pipe either, so there is no
                // destination to correctly deliver this to; dropping it is the safe outcome.
                if (TryGetResponseId(body, out var responseId)
                    && _requestSessionsById.TryRemove(responseId, out var owningSessionId)
                    && owningSessionId != GetCurrentSessionId())
                {
                    _logger.LogInformation(
                        "LspInterceptingPipe [Receive]: dropped response id={ResponseId} — belongs to " +
                        "abandoned session #{OwningSessionId}, current session is #{CurrentSessionId}.",
                        responseId, owningSessionId, GetCurrentSessionId());
                    continue;
                }

                var message = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                var result  = await RunInterceptorsAsync(message, _receiveInterceptors, ct).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                    await ForwardToCurrentVsWriterAsync(rawBytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInterceptingPipe [Receive] pump faulted.");
        }
        finally
        {
            // Complete whichever VS-facing pipe is current at shutdown time; abandoned earlier
            // sessions were already completed individually by CreateFreshVsFacingPipe.
            try { await GetCurrentToVsWriter().CompleteAsync().ConfigureAwait(false); }
            catch { /* best-effort at shutdown */ }
        }
    }

    /// <summary>
    /// Forwards one already-decoded frame to whichever VS-facing pipe is current, tolerating (log +
    /// continue, per <see cref="ReceivePumpAsync"/>'s remarks) a write failure against a pipe a
    /// concurrent <see cref="CreateFreshVsFacingPipe"/> call just abandoned and completed.
    /// </summary>
    private async Task ForwardToCurrentVsWriterAsync(byte[] rawFrame, CancellationToken ct)
    {
        try
        {
            await LspFrameCodec.WriteFrameAsync(GetCurrentToVsWriter(), rawFrame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LspInterceptingPipe [Receive]: forwarding a frame to the current VS-facing pipe failed " +
                "(tolerated -- likely raced a CreateFreshVsFacingPipe swap; the next frame goes to " +
                "whatever pipe is current then).");
        }
    }

    /// <summary>
    /// Session-scoped pump for the VS → server direction: reads one VS-facing session's
    /// <c>_fromVsPipe.Reader</c> and forwards to the real, persistent <see cref="_serverPipe"/>
    /// stdin. Unlike <see cref="ReceivePumpAsync"/>, ending this pump (for any reason, including an
    /// unhandled exception) is <b>not</b> fatal to anything beyond this one session — a fresh one
    /// replaces it on the next <see cref="CreateFreshVsFacingPipe"/> call.
    /// </summary>
    private async Task SendPumpAsync(PipeReader source, int sessionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await LspFrameCodec.ReadNextFrameAsync(source, ct).ConfigureAwait(false);
                if (frame is null)
                    break;

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await WriteFrameGuardedAsync(_serverPipe.Output, frame.RawBytes, lockDestination: true, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                // Record which session sent this request (issue #395), before forwarding, so a
                // late response arriving after this session has been abandoned can be recognised
                // and dropped instead of misdelivered to whatever session is current by then.
                if (TryGetRequestId(body, out var requestId))
                    _requestSessionsById[requestId] = sessionId;

                var message = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.Now);
                var result  = await RunInterceptorsAsync(message, _sendInterceptors, ct).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                {
                    await WriteFrameGuardedAsync(_serverPipe.Output, frame.RawBytes, lockDestination: true, ct)
                        .ConfigureAwait(false);

                    // Only after the frame has actually gone out, and only if it did (an interceptor
                    // that consumed `exit` means the server was never told to leave). Issue #555.
                    if (IsExitNotification(body))
                        MarkServerTerminated("VS sent `exit` on this connection");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown, or superseded by a fresh session */ }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Expected shutdown race (issue #165): Dispose() cancels the pumps and disposes
            // _injectLock without first awaiting an in-flight WriteFrameGuardedAsync call, so
            // this pump can still be inside _injectLock.WaitAsync when the semaphore gets
            // disposed out from under it. Benign — this pump loop was already exiting from the
            // same Dispose() call, and the server reports a graceful exit immediately after.
            // Logged at Debug rather than Error so shutdown doesn't produce misleading noise.
            _logger.LogDebug(
                "LspInterceptingPipe [Send] pump (session #{SessionId}) observed a disposed semaphore " +
                "during shutdown (benign).", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInterceptingPipe [Send] pump (session #{SessionId}) faulted.", sessionId);
        }
    }

    // ── Frame reader/writer ───────────────────────────────────────────────────
    //
    // The actual wire codec is LspFrameCodec (issue #587, step 1) — pure serialization with no
    // session state, extracted so it is independently testable against captured frames. What
    // stays here is orchestration that genuinely needs this instance's state: guarding a write
    // against the send pump's own concurrent writes to the same destination.

    /// <summary>
    /// Forwards a frame to <paramref name="writer"/>, taking <see cref="_injectLock"/> first when
    /// <paramref name="lockDestination"/> is set. The send pump's destination
    /// (<c>_serverPipe.Output</c>) is also written to directly by
    /// <see cref="SendNotificationToServerAsync"/>/<see cref="SendRequestToServerAsync"/> from
    /// other threads; without this, the pump's own passthrough write here could interleave with
    /// an injected write on the same unsynchronised <see cref="PipeWriter"/>, corrupting the
    /// framing.
    /// </summary>
    private async Task WriteFrameGuardedAsync(PipeWriter writer, byte[] rawFrame, bool lockDestination, CancellationToken ct)
    {
        if (!lockDestination)
        {
            await LspFrameCodec.WriteFrameAsync(writer, rawFrame, ct).ConfigureAwait(false);
            return;
        }

        await _injectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LspFrameCodec.WriteFrameAsync(writer, rawFrame, ct).ConfigureAwait(false);
        }
        finally
        {
            _injectLock.Release();
        }
    }

    // ── Interceptor pipeline ────────────────────────────────────────────────

    private async Task<LspInterceptorResult> RunInterceptorsAsync(
        LspMessage                            message,
        IReadOnlyList<ILspMessageInterceptor> interceptors,
        CancellationToken                     ct)
    {
        foreach (var interceptor in interceptors)
        {
            try
            {
                var result = await interceptor.InterceptAsync(message, ct).ConfigureAwait(false);
                if (result == LspInterceptorResult.Consume)
                    return LspInterceptorResult.Consume;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LspInterceptingPipe: interceptor {InterceptorType} threw.",
                    interceptor.GetType().Name);
            }
        }

        return LspInterceptorResult.PassThrough;
    }

    // ── Notification injection (VS → Server) ───────────────────────────────

    /// <summary>
    /// Encodes a JSON-RPC notification and writes it directly into the server-bound
    /// output stream, bypassing the VS-facing pipe.  Safe to call from any thread;
    /// uses <see cref="_injectLock"/> to serialise against the send pump.
    /// </summary>
    /// <param name="method">LSP method name, e.g. <c>reqnroll/projectLoaded</c>.</param>
    /// <param name="paramsJson">
    /// Already-serialized JSON string for the <c>params</c> field, or <c>null</c>/empty
    /// to omit the field.
    /// </param>
    public async Task SendNotificationToServerAsync(
        string method,
        string? paramsJson,
        CancellationToken cancellationToken)
    {
        if (_disposed) return;

        // Issue #555: after `exit` the server is on its way out, so this would write into a stream
        // nothing is reading. Observed in the wild: our own StepCodeLens/navigation-bar traffic kept
        // being injected for ~200ms after VS's `exit` and on through the following session.
        if (_serverTerminated)
        {
            _logger.LogInformation(
                "LspInterceptingPipe: refusing to inject notification {Method} — the server on this " +
                "connection has terminated.", method);
            return;
        }

        // Build the JSON-RPC notification frame.
        var body = string.IsNullOrEmpty(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

        var bodyBytes  = LspFrameCodec.Utf8NoBom.GetBytes(body);
        var headerText = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = LspFrameCodec.Utf8NoBom.GetBytes(headerText);

        await _injectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var memory = _serverPipe.Output.GetMemory(headerBytes.Length + bodyBytes.Length);
            headerBytes.CopyTo(memory);
            bodyBytes.CopyTo(memory.Slice(headerBytes.Length));
            _serverPipe.Output.Advance(headerBytes.Length + bodyBytes.Length);
            await _serverPipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "LspInterceptingPipe: injected notification {Method} ({ByteCount} bytes)", method, bodyBytes.Length);
        }
        finally
        {
            _injectLock.Release();
        }

        // Notify interceptors about the injected notification so it appears in the inspector log.
        // This runs outside the inject lock to avoid holding it during potentially-slow I/O.
        JObject? bodyObj = null;
        try { bodyObj = JObject.Parse(body); } catch { /* malformed — skip */ }
        if (bodyObj is not null)
        {
            var synthetic = new LspMessage(LspMessageDirection.Send, bodyObj, DateTimeOffset.Now);
            await RunInterceptorsAsync(synthetic, _sendInterceptors, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Request injection and response correlation (VS → Server → back) ──────

    /// <summary>
    /// Injects a JSON-RPC request into the server-bound stream and awaits the server's response.
    /// The response is <b>consumed</b> by the receive pump and never forwarded to VS.
    /// </summary>
    /// <param name="method">LSP method name, e.g. <c>textDocument/references</c>.</param>
    /// <param name="paramsJson">Already-serialized JSON for <c>params</c>, or <c>null</c> to omit it.</param>
    /// <returns>
    /// The <c>result</c> field of the server's response as a <see cref="JToken"/> (may be a
    /// <see cref="JArray"/>, <see cref="JObject"/>, or primitive), or <c>null</c> if the server
    /// returned a JSON-RPC error, the result was JSON null, or the operation was cancelled.
    /// </returns>
    public async Task<JToken?> SendRequestToServerAsync(
        string method,
        string? paramsJson,
        CancellationToken cancellationToken)
    {
        if (_disposed) return null;

        // Issue #555: see SendNotificationToServerAsync. A request is the worse case of the two —
        // it would await a response that can never arrive, so the caller blocks until its own token
        // trips rather than finding out immediately that there is no server.
        if (_serverTerminated)
        {
            _logger.LogInformation(
                "LspInterceptingPipe: refusing to inject request {Method} — the server on this " +
                "connection has terminated.", method);
            return null;
        }

        var id  = RequestIdPrefix + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        // Register cancellation before sending to avoid the race where the token is already
        // cancelled at the point we would have registered.
        var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            var body = string.IsNullOrEmpty(paramsJson)
                ? $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)}}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

            var bodyBytes   = LspFrameCodec.Utf8NoBom.GetBytes(body);
            var headerText  = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
            var headerBytes = LspFrameCodec.Utf8NoBom.GetBytes(headerText);

            await _injectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var memory = _serverPipe.Output.GetMemory(headerBytes.Length + bodyBytes.Length);
                headerBytes.CopyTo(memory);
                bodyBytes.CopyTo(memory.Slice(headerBytes.Length));
                _serverPipe.Output.Advance(headerBytes.Length + bodyBytes.Length);
                await _serverPipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "LspInterceptingPipe: injected request {Method} id={Id} ({ByteCount} bytes)", method, id, bodyBytes.Length);
            }
            finally
            {
                _injectLock.Release();
            }

            // Notify interceptors about the injected request (issue #491), the same way
            // SendNotificationToServerAsync already does, so it appears in the inspector log —
            // otherwise every owned-RPC request (e.g. reqnroll/resolveTestTargets) is invisible to
            // LspInspectorLogger even though it genuinely crossed the wire. Runs outside the inject
            // lock to avoid holding it during potentially-slow interceptor work; parsing the body we
            // just built cannot fail, so no try/catch is needed around it the way the receive-side
            // equivalent needs one around externally-sourced bytes.
            var injectedMessage = new LspMessage(LspMessageDirection.Send, JObject.Parse(body), DateTimeOffset.Now);
            await RunInterceptorsAsync(injectedMessage, _sendInterceptors, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "LspInterceptingPipe: request {Method} id={Id} cancelled", method, id);
            return null;
        }
        finally
        {
            reg.Dispose();
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="body"/> is a JSON-RPC response to one of our injected
    /// requests (identified purely by the <see cref="RequestIdPrefix"/> id, which only
    /// <see cref="SendRequestToServerAsync"/> ever generates — VS's own request ids are always
    /// plain integers). Pure check, no side effects — see <see cref="CompleteCorrelatedResponse"/>
    /// for actually consuming it. Split out (issue #491) so the caller can run the response
    /// through <c>_receiveInterceptors</c> for logging in between recognising and consuming it.
    /// </summary>
    private static bool TryGetCorrelatedResponseId(JObject body, out string id)
    {
        id = string.Empty;

        // A JSON-RPC response has an "id" and either "result" or "error", but no "method".
        if (body.ContainsKey("method")) return false;

        var idToken = body["id"];
        var idValue = idToken?.Value<string>();
        if (idValue is null || !idValue.StartsWith(RequestIdPrefix, StringComparison.Ordinal)) return false;

        id = idValue;
        return true;
    }

    /// <summary>
    /// Completes the awaiting <see cref="TaskCompletionSource{T}"/> for the injected request
    /// <paramref name="id"/> (when one is still registered) with <paramref name="body"/>'s result.
    /// Always called once <see cref="TryGetCorrelatedResponseId"/> recognises the response as ours
    /// — the frame is never forwarded to VS regardless of whether a pending TCS is still around to
    /// receive it.
    /// </summary>
    /// <remarks>
    /// Issue #401: a response must never be forwarded to VS just because
    /// <see cref="SendRequestToServerAsync"/>'s caller already gave up on it. That method's
    /// <c>finally</c> block removes the id from <see cref="_pendingRequests"/> as soon as its
    /// caller's <see cref="CancellationToken"/> fires — e.g. a <see cref="StepCodeLensService"/>
    /// request cancelled mid-reconnect — which can race the server's real response arriving a few
    /// milliseconds later. Previously that race made the caller treat this as "nothing left to
    /// complete," letting the response fall through to <see cref="ForwardToCurrentVsWriterAsync"/>
    /// and hand VS's JsonRpc a response to a request it never sent: the same
    /// <c>RemoteProtocolViolation: A response was received without a request having been sent</c>
    /// fatal error #395 fixed for VS's own peer-session responses, just triggered via this side
    /// channel instead. Since the id prefix alone proves the response is ours, it is always safe
    /// (and correct) to consume it here regardless of whether a pending TCS is still around to
    /// receive it.
    /// </remarks>
    private void CompleteCorrelatedResponse(string id, JObject body)
    {
        if (_pendingRequests.TryRemove(id, out var tcs))
        {
            if (body.ContainsKey("error"))
                tcs.TrySetResult(null);
            else
                tcs.TrySetResult(body["result"]);

            _logger.LogInformation(
                "LspInterceptingPipe: consumed correlated response id={Id}", id);
        }
        else
        {
            _logger.LogInformation(
                "LspInterceptingPipe: dropped response id={Id} — no pending request (already " +
                "cancelled/removed), but the {Prefix} id proves it's ours; forwarding it to VS " +
                "would be an unmatched response and fatally close the connection (issue #401).",
                id, RequestIdPrefix);
        }
    }

    /// <summary>
    /// True if <paramref name="body"/> is a JSON-RPC <b>request</b> (has both <c>id</c> and
    /// <c>method</c> — as opposed to a notification, which has no <c>id</c>, or a response, which
    /// has no <c>method</c>). Used by <see cref="SendPumpAsync"/> to record which VS-facing session
    /// sent each request (issue #395).
    /// </summary>
    private static bool TryGetRequestId(JObject body, out string id)
    {
        id = string.Empty;
        if (!body.ContainsKey("method")) return false;

        var idToken = body["id"];
        var idValue = idToken?.Value<string>();
        if (idValue is null) return false;

        id = idValue;
        return true;
    }

    /// <summary>
    /// True if <paramref name="body"/> is a JSON-RPC <b>response</b> (has <c>id</c>, no
    /// <c>method</c>). Mirrors <see cref="TryGetCorrelatedResponseId"/>'s own shape check, for
    /// VS's own (non-<see cref="RequestIdPrefix"/>) request ids.
    /// </summary>
    private static bool TryGetResponseId(JObject body, out string id)
    {
        id = string.Empty;
        if (body.ContainsKey("method")) return false;

        var idToken = body["id"];
        var idValue = idToken?.Value<string>();
        if (idValue is null) return false;

        id = idValue;
        return true;
    }

    private static string JsonEscape(string value)
        => Newtonsoft.Json.JsonConvert.ToString(value); // produces "\"value\""

    // ── IDisposable ─────────────────────────────────────────────────────────

    /// <summary>Cancels the pump tasks, faults any in-flight injected requests, and completes the current VS-facing pipe.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _linkedCts?.Cancel();
        _linkedCts?.Dispose();
        _cts.Dispose();
        _injectLock.Dispose();

        lock (_vsPipeSwapLock)
        {
            _currentSendPumpCts?.Cancel();
            _currentSendPumpCts?.Dispose();
        }

        // Fault any in-flight injected requests so callers don't hang.
        foreach (var kv in _pendingRequests)
            kv.Value.TrySetCanceled();
        _pendingRequests.Clear();

        try { GetCurrentToVsWriter().Complete(); } catch { /* best-effort */ }
    }

    // ── Inner helper ────────────────────────────────────────────────────────

    /// <summary>Adapts a <see cref="PipeReader"/> / <see cref="PipeWriter"/> pair into an <see cref="IDuplexPipe"/>.</summary>
    private sealed class DuplexPipeAdapter : IDuplexPipe
    {
        public DuplexPipeAdapter(PipeReader input, PipeWriter output)
        {
            Input  = input;
            Output = output;
        }

        public PipeReader Input  { get; }
        public PipeWriter Output { get; }
    }
}
