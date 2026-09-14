using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// The session-scoped pump for the VS → server direction (issue #587, step 3): reads one VS-facing
/// session's pipe and forwards to the real, persistent server stdin.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ending this pump is not fatal beyond its own session.</b> That is the asymmetry with
/// <see cref="ServerToVsPump"/>, and it is why the two are separate types rather than two methods
/// with a shared shape: one instance exists per <c>CreateServerConnectionAsync</c> call, a fresh one
/// replaces it on the next, and an unhandled failure here costs only the session it belongs to.
/// </para>
/// <para>
/// It is also the direction that ends the connection. VS closes an LSP session with
/// <c>shutdown</c> then <c>exit</c> — on a solution close, not only at IDE shutdown (issue #555) —
/// and <c>exit</c> travels this path.
/// </para>
/// </remarks>
internal sealed class VsToServerPump
{
    private readonly PipeReader          _fromVs;
    private readonly int                 _sessionId;
    private readonly ServerChannel       _serverChannel;
    private readonly VsSessionRouter     _router;
    private readonly InterceptorPipeline _sendInterceptors;
    private readonly Action<string>      _markTerminated;
    private readonly Func<bool>          _connectionDisposed;
    private readonly ILogger             _logger;

    /// <summary>Creates the pump for one VS-facing session.</summary>
    /// <param name="markTerminated">
    /// Invoked only once VS's <c>exit</c> has <em>actually</em> been written to the server
    /// (issue #555).
    /// </param>
    /// <param name="connectionDisposed">Whether the owning connection has been disposed; used to classify a shutdown-race exception as benign.</param>
    public VsToServerPump(
        PipeReader          fromVs,
        int                 sessionId,
        ServerChannel       serverChannel,
        VsSessionRouter     router,
        InterceptorPipeline sendInterceptors,
        Action<string>      markTerminated,
        Func<bool>          connectionDisposed,
        ILogger             logger)
    {
        _fromVs             = fromVs           ?? throw new ArgumentNullException(nameof(fromVs));
        _sessionId          = sessionId;
        _serverChannel      = serverChannel    ?? throw new ArgumentNullException(nameof(serverChannel));
        _router             = router           ?? throw new ArgumentNullException(nameof(router));
        _sendInterceptors   = sendInterceptors ?? throw new ArgumentNullException(nameof(sendInterceptors));
        _markTerminated     = markTerminated   ?? throw new ArgumentNullException(nameof(markTerminated));
        _connectionDisposed = connectionDisposed ?? throw new ArgumentNullException(nameof(connectionDisposed));
        _logger             = logger           ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs until this session's pipe ends or <paramref name="cancellationToken"/> fires (which happens when the session is superseded).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await LspFrameCodec.ReadNextFrameAsync(_fromVs, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;

                if (frame.HasMalformedHeader)
                {
                    _logger.LogWarning(
                        "VsToServerPump (session #{SessionId}): skipped a malformed header block from VS " +
                        "({Header}); resynchronising on the next frame.", _sessionId, frame.MalformedHeaderText);
                    continue;
                }

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await _serverChannel.ForwardAsync(frame.RawBytes, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Record which session sent this request (issue #395), before forwarding, so a late
                // response arriving after this session has been abandoned can be recognised and
                // dropped instead of misdelivered to whatever session is current by then.
                if (LspJsonRpc.TryGetRequestId(body, out var requestId))
                    _router.RecordOutboundRequest(requestId, _sessionId);

                var message = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.Now);
                var result  = await _sendInterceptors.RunAsync(message, cancellationToken).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                {
                    await _serverChannel.ForwardAsync(frame.RawBytes, cancellationToken).ConfigureAwait(false);

                    // Only after the frame has actually gone out, and only if it did (an interceptor
                    // that consumed `exit` means the server was never told to leave). Issue #555.
                    if (LspJsonRpc.IsExitNotification(body))
                        _markTerminated("VS sent `exit` on this connection");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown, or superseded by a fresh session */ }
        catch (ObjectDisposedException) when (_connectionDisposed())
        {
            // Expected shutdown race (issue #165). Its original cause — disposing the inject
            // semaphore out from under an in-flight write — is gone: ServerChannel owns that
            // semaphore now and never disposes it. What remains is the connection disposing the
            // cancellation token sources this pump is registered on. Logged at Debug so shutdown
            // produces no misleading noise.
            _logger.LogDebug(
                "VsToServerPump (session #{SessionId}) observed a disposed object during shutdown (benign).",
                _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VsToServerPump (session #{SessionId}) faulted.", _sessionId);
        }
    }
}
