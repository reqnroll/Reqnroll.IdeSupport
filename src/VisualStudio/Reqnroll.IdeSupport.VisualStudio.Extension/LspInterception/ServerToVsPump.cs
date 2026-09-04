using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// The persistent, single-instance pump for the server → VS direction (issue #587, step 3): reads
/// the real server process's stdout for the lifetime of the connection and forwards each frame to
/// whichever VS-facing pipe is <i>current</i> at that moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>This pump must never exit while the server process is alive.</b> That is the asymmetry with
/// <see cref="VsToServerPump"/> and the most important property of this type: a failure here would
/// silently stop relaying server output to <em>every future VS session</em>, not just the current
/// one, whereas a send pump's failure costs only its own session. Everything that could plausibly
/// go wrong per frame is therefore logged and tolerated rather than allowed to end the loop —
/// a malformed header block, a normalizer bug, an interceptor that throws, and a write against a
/// destination a concurrent session swap has just abandoned.
/// </para>
/// <para>
/// The destination is looked up per frame (<see cref="VsFacingSessionManager.CurrentToVsWriter"/>)
/// rather than captured once, since <see cref="VsFacingSessionManager.StartNewSession"/> can replace
/// it from under this long-running pump at any time (issue #156).
/// </para>
/// </remarks>
internal sealed class ServerToVsPump
{
    private readonly PipeReader             _serverStdout;
    private readonly VsFacingSessionManager _sessions;
    private readonly LspRequestCorrelator   _correlator;
    private readonly VsSessionRouter        _router;
    private readonly InterceptorPipeline    _receiveInterceptors;
    private readonly Func<bool>             _connectionDisposed;
    private readonly ILogger                _logger;

    /// <summary>Creates the pump over the server's stdout and the collaborators it routes through.</summary>
    /// <param name="connectionDisposed">
    /// Whether the owning connection has been disposed. A write failure is tolerated while the
    /// connection is live, but not once it is being torn down — there the exception is rethrown so
    /// this pump ends rather than looping against a destination that will never accept a frame again.
    /// </param>
    public ServerToVsPump(
        PipeReader             serverStdout,
        VsFacingSessionManager sessions,
        LspRequestCorrelator   correlator,
        VsSessionRouter        router,
        InterceptorPipeline    receiveInterceptors,
        Func<bool>             connectionDisposed,
        ILogger                logger)
    {
        _serverStdout        = serverStdout        ?? throw new ArgumentNullException(nameof(serverStdout));
        _sessions            = sessions            ?? throw new ArgumentNullException(nameof(sessions));
        _correlator          = correlator          ?? throw new ArgumentNullException(nameof(correlator));
        _router              = router              ?? throw new ArgumentNullException(nameof(router));
        _receiveInterceptors = receiveInterceptors ?? throw new ArgumentNullException(nameof(receiveInterceptors));
        _connectionDisposed  = connectionDisposed  ?? throw new ArgumentNullException(nameof(connectionDisposed));
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs until the server's stdout ends or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await LspFrameCodec.ReadNextFrameAsync(_serverStdout, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break; // server process ended its stdout -- genuinely fatal, nothing more to relay.

                if (frame.HasMalformedHeader)
                {
                    // A header block with no usable Content-Length: nothing can be forwarded (the
                    // body's extent is unknowable), but this pump must survive it -- see the class
                    // remarks on why exiting here would silently end LSP for every future session.
                    _logger.LogWarning(
                        "ServerToVsPump: skipped a malformed header block from the server ({Header}); " +
                        "resynchronising on the next frame.", frame.MalformedHeaderText);
                    continue;
                }

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await ForwardToCurrentVsWriterAsync(frame.RawBytes, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var rawBytes = NormalizeDriveLetters(frame, body);

                // Consume correlated responses before *forwarding* to VS (they must never reach
                // VS's own JsonRpc, which never sent them) — but still run them through the receive
                // interceptors first (issue #491) so LspInspectorLogger sees this traffic like
                // everything else on the pipe. Without this, every owned-RPC response (e.g.
                // reqnroll/resolveTestTargets) is invisible to the inspector log even though it
                // genuinely crossed the wire, which made a real N+1 request-storm bug look like
                // near-total silence when first diagnosed.
                if (LspRequestCorrelator.IsOwnedResponse(body, out var correlatedId))
                {
                    var correlatedMessage = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                    await _receiveInterceptors.RunAsync(correlatedMessage, cancellationToken).ConfigureAwait(false);

                    _correlator.Consume(correlatedId, body);
                    continue;
                }

                // Drop a response that does not belong to the session currently in effect (issue
                // #395) — forwarding it would hand an unmatched response to a JsonRpc instance that
                // never sent that request, which VS treats as a fatal protocol violation and closes
                // the brand-new connection over. Nothing is listening on an abandoned session's own
                // pipe either, so there is no destination to correctly deliver it to.
                var currentSessionId = _sessions.CurrentSessionId;
                if (_router.Route(body, currentSessionId, out var owningSessionId) == ResponseRouting.DropAbandoned)
                {
                    _logger.LogInformation(
                        "ServerToVsPump: dropped response — owning session #{OwningSessionId} " +
                        "(0 = no longer tracked) is not the current session #{CurrentSessionId}.",
                        owningSessionId, currentSessionId);
                    continue;
                }

                var message = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                var result  = await _receiveInterceptors.RunAsync(message, cancellationToken).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                    await ForwardToCurrentVsWriterAsync(rawBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ServerToVsPump faulted.");
        }
        finally
        {
            // Complete whichever VS-facing pipe is current at shutdown time; abandoned earlier
            // sessions were already completed individually when they were abandoned.
            try { await _sessions.CurrentToVsWriter.CompleteAsync().ConfigureAwait(false); }
            catch { /* best-effort at shutdown */ }
        }
    }

    /// <summary>
    /// Rewrites drive-letter casing in <paramref name="body"/> and returns the bytes to forward
    /// (re-encoded only if anything changed).
    /// </summary>
    /// <remarks>
    /// OmniSharp's <c>DocumentUri</c> unconditionally lowercases drive letters, but VS tracks
    /// documents using the project system's original (upper-case) casing. This runs before
    /// correlation and before any interceptor sees the message, so every server → VS path — owned-RPC
    /// responses consumed here, and messages forwarded on to VS's own LSP client — gets a
    /// VS-matching URI. Guarded like an interceptor: a bug here must degrade to "URI casing unfixed
    /// for this message", never sever the pipe — and unlike an interceptor fault, this runs before
    /// <see cref="LspInspectorLogger"/> sees the message, so a silent failure would leave no trace
    /// in the wire log.
    /// </remarks>
    private byte[] NormalizeDriveLetters(LspFrameCodec.LspFrame frame, Newtonsoft.Json.Linq.JObject body)
    {
        try
        {
            if (DriveLetterUriNormalizer.NormalizeInPlace(body))
                return LspFrameCodec.EncodeFrame(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ServerToVsPump: DriveLetterUriNormalizer threw on message {Body}", body.ToString());
        }

        return frame.RawBytes;
    }

    /// <summary>
    /// Forwards one already-encoded frame to whichever VS-facing pipe is current, tolerating (log +
    /// continue, per the class remarks) a write failure against a pipe a concurrent session swap
    /// just abandoned and completed.
    /// </summary>
    private async Task ForwardToCurrentVsWriterAsync(byte[] rawFrame, CancellationToken cancellationToken)
    {
        try
        {
            await LspFrameCodec.WriteFrameAsync(_sessions.CurrentToVsWriter, rawFrame, cancellationToken)
                               .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_connectionDisposed()) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ServerToVsPump: forwarding a frame to the current VS-facing pipe failed (tolerated -- " +
                "likely raced a session swap; the next frame goes to whatever pipe is current then).");
        }
    }
}
