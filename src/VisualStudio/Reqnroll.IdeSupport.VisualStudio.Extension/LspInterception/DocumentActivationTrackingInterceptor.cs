using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Drives <see cref="DocumentActivationState"/>'s <c>didOpen</c>/<c>didClose</c> transitions
/// (issue #85) from the VS→Server send pipeline, and — in the one case where a
/// <c>reqnroll/documentActivated</c> send needs to happen in direct response to the very
/// <c>didOpen</c> message being intercepted — sends it itself rather than racing a separate
/// caller against the pump's own forwarding write.
/// </summary>
/// <remarks>
/// <para>
/// The common case (a tab already open when the user switches to it) is handled entirely
/// outside this interceptor: a VS-side <c>WindowActivated</c> listener calls
/// <see cref="DocumentActivationState.OnWindowActivated"/> directly and, on
/// <see cref="DocumentActivationAction.SendNow"/>, sends the notification itself via
/// <see cref="LspInterceptingPipe.SendNotificationToServerAsync"/> — safe because by that point
/// <c>didOpen</c> has already been forwarded (the state is <c>Opened</c>, not
/// <c>ActivationPending</c>), so there is nothing to sequence against.
/// </para>
/// <para>
/// The rarer case this interceptor exists for: <c>WindowActivated</c> fires before
/// <c>didOpen</c> arrives (<see cref="DocumentActivationPhase.ActivationPending"/>). When
/// <c>didOpen</c> finally shows up, <see cref="DocumentActivationState.OnDidOpen"/> returns
/// <see cref="DocumentActivationAction.SendNow"/> — but the server must see <c>didOpen</c>
/// <em>before</em> <c>documentActivated</c> (the handler no-ops if it can't find an open
/// buffer for the URI). Consuming the message here and re-forwarding it manually, followed by
/// the activation notification, both via the same awaited
/// <see cref="LspInterceptingPipe.SendNotificationToServerAsync"/> calls, keeps the two writes
/// in the wire order this interceptor chose rather than leaving it to a race between the pump's
/// own passthrough write and a separately-scheduled injected send.
/// </para>
/// </remarks>
internal sealed class DocumentActivationTrackingInterceptor : ILspMessageInterceptor
{
    private readonly DocumentActivationState                        _state;
    private readonly Func<LspInterceptingPipe?>                      _getPipe;
    private readonly ILogger<DocumentActivationTrackingInterceptor>  _logger;

    // Issue #187: LspInterceptingPipe.SendNotificationToServerAsync re-runs the full
    // send-interceptor pipeline on a synthetic copy of whatever it just wrote, purely so the
    // injected message shows up in the inspector log. That means the didOpen we re-forward
    // below calls back into this very InterceptAsync while the first call is still awaiting it —
    // re-entrantly, on the same path. Without a guard, that second call would run
    // _state.OnDidOpen(path) again and (since phase is now Activated) incorrectly reset it back
    // to Opened. This set tracks paths we're currently in the middle of self-re-forwarding, so
    // the re-entrant call can recognize itself and skip the state-machine call.
    private readonly HashSet<string> _selfForwardedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object          _selfForwardedLock  = new();

    /// <summary>Creates the interceptor over the shared activation-state tracker and a deferred pipe accessor.</summary>
    public DocumentActivationTrackingInterceptor(
        DocumentActivationState       state,
        Func<LspInterceptingPipe?>    getPipe,
        ILogger<DocumentActivationTrackingInterceptor> logger)
    {
        _state   = state   ?? throw new ArgumentNullException(nameof(state));
        _getPipe = getPipe ?? throw new ArgumentNullException(nameof(getPipe));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LspInterceptorResult> InterceptAsync(
        LspMessage        message,
        CancellationToken cancellationToken)
    {
        if (message.Method == "textDocument/didClose")
        {
            if (UriToFeatureFilePath(message) is { } closedPath)
            {
                _state.OnDidClose(closedPath);
                _logger.LogTrace(
                    "DocumentActivationTrackingInterceptor: didClose for {FileName} — activation state reset.",
                    Path.GetFileName(closedPath));
            }
            return LspInterceptorResult.PassThrough;
        }

        if (message.Method != "textDocument/didOpen")
            return LspInterceptorResult.PassThrough;

        if (UriToFeatureFilePath(message) is not { } path)
            return LspInterceptorResult.PassThrough;

        // Re-entrant call from our own re-forwarded didOpen below (see _selfForwardedPaths'
        // doc comment) — the state machine already saw this didOpen once; don't run it again.
        lock (_selfForwardedLock)
        {
            if (_selfForwardedPaths.Contains(path))
            {
                _logger.LogTrace(
                    "DocumentActivationTrackingInterceptor: didOpen for {FileName} — re-entrant self-forwarded copy, passthrough.",
                    Path.GetFileName(path));
                return LspInterceptorResult.PassThrough;
            }
        }

        var action = _state.OnDidOpen(path);
        if (action != DocumentActivationAction.SendNow)
        {
            _logger.LogTrace(
                "DocumentActivationTrackingInterceptor: didOpen for {FileName} — no pending activation, passthrough.",
                Path.GetFileName(path));
            return LspInterceptorResult.PassThrough;
        }

        var pipe = _getPipe();
        if (pipe is null)
        {
            _logger.LogWarning(
                "DocumentActivationTrackingInterceptor: pipe not available for {FileName}; letting didOpen pass through without an activation notification.",
                Path.GetFileName(path));
            return LspInterceptorResult.PassThrough;
        }

        // Re-forward didOpen ourselves, then send documentActivated — both awaited in order —
        // so the server sees the buffer before it's asked to recompute against it. Consuming
        // here stops the pump's own passthrough write for this message.
        var paramsJson = message.Body["params"]?.ToString();
        var docUri     = message.Body["params"]?["textDocument"]?["uri"]?.Value<string>();

        lock (_selfForwardedLock) { _selfForwardedPaths.Add(path); }
        try
        {
            await pipe.SendNotificationToServerAsync("textDocument/didOpen", paramsJson, cancellationToken)
                      .ConfigureAwait(false);
        }
        finally
        {
            lock (_selfForwardedLock) { _selfForwardedPaths.Remove(path); }
        }

        _logger.LogDebug(
            "DocumentActivationTrackingInterceptor: activation preceded didOpen for {FileName}; sending reqnroll/documentActivated now.",
            Path.GetFileName(path));

        var activatedParamsJson = $"{{\"uri\":{Newtonsoft.Json.JsonConvert.ToString(docUri)}}}";
        await pipe.SendNotificationToServerAsync("reqnroll/documentActivated", activatedParamsJson, cancellationToken)
                  .ConfigureAwait(false);

        return LspInterceptorResult.Consume;
    }

    private static string? UriToFeatureFilePath(LspMessage message)
    {
        var uri = message.Body["params"]?["textDocument"]?["uri"]?.Value<string>();
        if (uri is null || !uri.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "file")
            return null;
        return parsed.LocalPath;
    }
}
