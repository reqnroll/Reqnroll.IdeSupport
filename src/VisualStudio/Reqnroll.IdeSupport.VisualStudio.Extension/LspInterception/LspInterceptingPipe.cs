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
///                 VsToServerPump      ServerToVsPump
///                        │                  │
///                        ▼                  │
///              Server stdin PipeWriter   Server stdout PipeReader
/// </code>
/// Each pump reads raw LSP frames (<c>Content-Length: N\r\n\r\nBODY</c>), parses them to
/// <see cref="LspMessage"/>, runs the relevant interceptor list, then — if no interceptor consumed
/// the message — forwards.
/// </para>
/// <para>
/// After the decomposition in issue #587 this type is the <b>composition root</b>: it builds the
/// collaborators, keeps the public surface its ten consuming services depend on, and owns nothing
/// but the wiring between them.
/// </para>
/// <list type="table">
///   <item><term><see cref="LspFrameCodec"/></term><description>the wire format (step 1)</description></item>
///   <item><term><see cref="ServerChannel"/></term><description>the server's stdin, the lock serialising writes to it, and the #555 termination flag</description></item>
///   <item><term><see cref="LspRequestCorrelator"/></term><description>owned-RPC ids and their waiters (#401)</description></item>
///   <item><term><see cref="VsSessionRouter"/></term><description>which session owns each outstanding response (#395)</description></item>
///   <item><term><see cref="VsFacingSessionManager"/></term><description>the VS-facing pipe pairs and their generations (#156)</description></item>
///   <item><term><see cref="InterceptorPipeline"/></term><description>one direction's interceptor list</description></item>
///   <item><term><see cref="ServerToVsPump"/> / <see cref="VsToServerPump"/></term><description>the two loops</description></item>
/// </list>
/// <para>
/// The two pumps are deliberately separate types because their failure semantics differ and that
/// difference is load-bearing: the receive pump is persistent and shared by every future VS session,
/// so it must never exit while the server lives; a send pump belongs to one session and a fresh one
/// replaces it on the next <see cref="CreateFreshVsFacingPipe"/> call.
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

        // Server stdout → ServerToVsPump reads _serverPipe.Input → the current VS-facing writer → VS
        // reads that pipe's Reader. The destination is looked up per frame
        // (VsFacingSessionManager.CurrentToVsWriter) rather than captured once, since a new session
        // can swap it out from under this long-running pump at any time.
        _receivePump = new ServerToVsPump(
            _serverPipe.Input, _sessions, _correlator, _router, _receiveInterceptors,
            () => _disposed, _logger).RunAsync(_lifetimeToken);

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
        _sessions.AttachSendPump(
            new VsToServerPump(
                session.FromVsReader, session.SessionId, _serverChannel, _router, _sendInterceptors,
                MarkServerTerminated, () => _disposed, _logger)
            .RunAsync(session.SessionToken));

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
