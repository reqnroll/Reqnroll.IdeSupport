using System;
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
    private readonly IDuplexPipe                  _serverPipe;
    private readonly InterceptorPipeline          _sendInterceptors;
    private readonly InterceptorPipeline          _receiveInterceptors;
    private readonly ILogger<LspInterceptingPipe> _logger;

    // ── Extracted collaborators (issue #587, step 2) ─────────────────────────
    // The write side of the server connection (its stdin writer, the lock serialising every write to
    // it, and the #555 termination flag), the owned-RPC correlation, and the #395 peer-session
    // routing each now live in their own type. What stays here is the orchestration between them.
    private readonly ServerChannel          _serverChannel;
    private readonly LspRequestCorrelator   _correlator;
    private readonly VsSessionRouter        _router;
    private readonly VsFacingSessionManager _sessions;

    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationTokenSource? _linkedCts;
    private CancellationToken        _lifetimeToken;
    private Task?                    _receivePump;

    private bool _disposed;

    /// <summary>
    /// <see langword="true"/> once the server behind this pipe has been asked to <c>exit</c> (or is
    /// otherwise known to be gone), meaning this connection can never serve another request.
    /// </summary>
    /// <remarks>
    /// Issue #555: VS ends an LSP session by sending <c>shutdown</c> then <c>exit</c> — which it does
    /// on a solution close, not only at IDE shutdown. Those go through to the real server, which
    /// obeys and terminates. <see cref="LspServerConnectionService"/> reads this to know the
    /// connection is spent and a fresh server must be launched for the next session. The flag itself
    /// lives on <see cref="ServerChannel"/>, which is what acts on it.
    /// </remarks>
    public bool ServerTerminated => _serverChannel.IsTerminated;

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
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
        _sendInterceptors    = new InterceptorPipeline(
            sendInterceptors    ?? throw new ArgumentNullException(nameof(sendInterceptors)), logger);
        _receiveInterceptors = new InterceptorPipeline(
            receiveInterceptors ?? throw new ArgumentNullException(nameof(receiveInterceptors)), logger);

        _serverChannel = new ServerChannel(_serverPipe.Output, logger);
        _correlator    = new LspRequestCorrelator(logger);
        _router        = new VsSessionRouter();
        _sessions      = new VsFacingSessionManager(logger);
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

        // Server stdout → ReceivePump reads _serverPipe.Input → the current VS-facing writer → VS
        // reads that pipe's Reader. The destination is looked up per frame
        // (VsFacingSessionManager.CurrentToVsWriter) rather than captured once, since a new session
        // can swap it out from under this long-running pump at any time.
        _receivePump = ReceivePumpAsync(_lifetimeToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a brand-new local VS-facing <see cref="Pipe"/> pair and returns the
    /// <see cref="IDuplexPipe"/> to hand to VS as the <c>CreateServerConnectionAsync</c> result.
    /// </summary>
    /// <remarks>
    /// The pipes themselves, the generation counter, and abandoning the previous session belong to
    /// <see cref="VsFacingSessionManager"/> (issue #156 — see its remarks). What is left here is the
    /// wiring that needs the rest of this connection: bounding the router's tracking, and starting a
    /// send pump for the new session. The connection to the real server process is untouched.
    /// </remarks>
    public IDuplexPipe CreateFreshVsFacingPipe()
    {
        var session = _sessions.StartNewSession(_lifetimeToken);

        // Bound the router's growth (issue #395): a request that's still in flight when its session
        // gets abandoned and never receives a response (e.g. genuinely dropped, not just delayed)
        // would otherwise leak its entry forever. Two generations is enough slack for the straggler
        // this tracking exists to catch — a response landing shortly after its own session was
        // abandoned (like the shutdown response that motivated the fix) — without holding entries
        // from sessions abandoned long ago.
        _router.PurgeOlderThan(minimumLiveSessionId: session.SessionId - 1);

        // VS → server direction for this session only.
        _sessions.AttachSendPump(SendPumpAsync(session.FromVsReader, session.SessionId, session.SessionToken));

        return session.VsFacing;
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
        _serverChannel.MarkTerminated(reason);
        _correlator.ReleaseAll();
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

                if (frame.HasMalformedHeader)
                {
                    // A header block with no usable Content-Length: nothing can be forwarded (the
                    // body's extent is unknowable), but this pump must survive it -- see the remarks
                    // above on why exiting here would silently end LSP for every future session.
                    _logger.LogWarning(
                        "LspInterceptingPipe [Receive]: skipped a malformed header block from the server " +
                        "({Header}); resynchronising on the next frame.", frame.MalformedHeaderText);
                    continue;
                }

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
                // Guarded like an interceptor (see InterceptorPipeline.RunAsync): a bug here must
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
                if (LspRequestCorrelator.IsOwnedResponse(body, out var correlatedId))
                {
                    var correlatedMessage = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                    await _receiveInterceptors.RunAsync(correlatedMessage, ct).ConfigureAwait(false);

                    _correlator.Consume(correlatedId, body);
                    continue;
                }

                // Drop a response whose matching request was sent by a since-abandoned VS-facing
                // session (issue #395) — forwarding it to whichever session is current would hand
                // an unmatched response to a JsonRpc instance that never sent that request, which
                // VS treats as a fatal protocol violation and closes the brand-new connection over.
                // Nothing is listening on the abandoned session's own pipe either, so there is no
                // destination to correctly deliver this to; dropping it is the safe outcome.
                var currentSessionId = _sessions.CurrentSessionId;
                if (_router.Route(body, currentSessionId, out var owningSessionId) == ResponseRouting.DropAbandoned)
                {
                    _logger.LogInformation(
                        "LspInterceptingPipe [Receive]: dropped response — owning session #{OwningSessionId} " +
                        "(0 = no longer tracked) is not the current session #{CurrentSessionId}.",
                        owningSessionId, currentSessionId);
                    continue;
                }

                var message = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                var result  = await _receiveInterceptors.RunAsync(message, ct).ConfigureAwait(false);

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
            try { await _sessions.CurrentToVsWriter.CompleteAsync().ConfigureAwait(false); }
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
            await LspFrameCodec.WriteFrameAsync(_sessions.CurrentToVsWriter, rawFrame, ct).ConfigureAwait(false);
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
    /// VS → server reader and forwards to the real, persistent <see cref="_serverPipe"/>
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

                if (frame.HasMalformedHeader)
                {
                    _logger.LogWarning(
                        "LspInterceptingPipe [Send] (session #{SessionId}): skipped a malformed header block " +
                        "from VS ({Header}); resynchronising on the next frame.",
                        sessionId, frame.MalformedHeaderText);
                    continue;
                }

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await _serverChannel.ForwardAsync(frame.RawBytes, ct).ConfigureAwait(false);
                    continue;
                }

                // Record which session sent this request (issue #395), before forwarding, so a
                // late response arriving after this session has been abandoned can be recognised
                // and dropped instead of misdelivered to whatever session is current by then.
                if (LspJsonRpc.TryGetRequestId(body, out var requestId))
                    _router.RecordOutboundRequest(requestId, sessionId);

                var message = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.Now);
                var result  = await _sendInterceptors.RunAsync(message, ct).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                {
                    await _serverChannel.ForwardAsync(frame.RawBytes, ct).ConfigureAwait(false);

                    // Only after the frame has actually gone out, and only if it did (an interceptor
                    // that consumed `exit` means the server was never told to leave). Issue #555.
                    if (LspJsonRpc.IsExitNotification(body))
                        MarkServerTerminated("VS sent `exit` on this connection");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown, or superseded by a fresh session */ }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Expected shutdown race (issue #165). Its original cause — Dispose() disposing the
            // inject semaphore out from under an in-flight write — is gone: ServerChannel owns that
            // semaphore now and never disposes it (see its remarks). The catch stays because
            // Dispose() also disposes the cancellation token sources the pumps are registered on,
            // which can surface the same exception on its own; without it a benign shutdown race
            // would be logged as an Error. Logged at Debug so shutdown produces no misleading noise.
            _logger.LogDebug(
                "LspInterceptingPipe [Send] pump (session #{SessionId}) observed a disposed object " +
                "during shutdown (benign).", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInterceptingPipe [Send] pump (session #{SessionId}) faulted.", sessionId);
        }
    }

    // ── Frame reader/writer ───────────────────────────────────────────────────
    //
    // The wire codec is LspFrameCodec (issue #587, step 1) and the guarded write to the server is
    // ServerChannel (step 2) — nothing framing-related is left in this type.

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

        var body = LspJsonRpc.BuildNotification(method, paramsJson);

        // Refused outright once the server has terminated (issue #555) — see ServerChannel.
        if (!await _serverChannel.InjectAsync(LspFrameCodec.Encode(body), method, cancellationToken)
                                 .ConfigureAwait(false))
            return;

        // Notify interceptors about the injected notification so it appears in the inspector log.
        // This runs outside the channel's write lock to avoid holding it during potentially-slow I/O.
        JObject? bodyObj = null;
        try { bodyObj = JObject.Parse(body); } catch { /* malformed — skip */ }
        if (bodyObj is not null)
        {
            var synthetic = new LspMessage(LspMessageDirection.Send, bodyObj, DateTimeOffset.Now);
            await _sendInterceptors.RunAsync(synthetic, cancellationToken).ConfigureAwait(false);
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

        using var pending = _correlator.Begin(cancellationToken);
        try
        {
            var body = LspJsonRpc.BuildRequest(pending.Id, method, paramsJson);

            // Issue #555: a request is the worse of the two injection cases — it would await a
            // response that can never arrive, so the caller blocks until its own token trips rather
            // than finding out immediately that there is no server.
            if (!await _serverChannel.InjectAsync(LspFrameCodec.Encode(body), method, cancellationToken)
                                     .ConfigureAwait(false))
                return null;

            // Notify interceptors about the injected request (issue #491), the same way
            // SendNotificationToServerAsync already does, so it appears in the inspector log —
            // otherwise every owned-RPC request (e.g. reqnroll/resolveTestTargets) is invisible to
            // LspInspectorLogger even though it genuinely crossed the wire. Runs outside the
            // channel's write lock to avoid holding it during potentially-slow interceptor work;
            // parsing the body we just built cannot fail, so no try/catch is needed around it the
            // way the receive-side equivalent needs one around externally-sourced bytes.
            var injectedMessage = new LspMessage(LspMessageDirection.Send, JObject.Parse(body), DateTimeOffset.Now);
            await _sendInterceptors.RunAsync(injectedMessage, cancellationToken).ConfigureAwait(false);

            return await pending.Response.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "LspInterceptingPipe: request {Method} id={Id} cancelled", method, pending.Id);
            return null;
        }
    }

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

        // Cancels the current session's send pump and completes the current server → VS writer.
        _sessions.Dispose();

        // Fault any in-flight injected requests so callers don't hang.
        _correlator.CancelAll();
    }
}
