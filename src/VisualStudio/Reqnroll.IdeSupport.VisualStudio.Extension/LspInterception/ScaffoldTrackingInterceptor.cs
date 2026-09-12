using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Tracks the creation of scaffolded <c>.cs</c> step-definition files and ensures the LSP
/// server's membership index is updated before <c>textDocument/didOpen</c> arrives.
///
/// <para>
/// When VS receives a code-action response that contains a <c>CreateFile</c> document change
/// for a <c>.cs</c> file (Receive direction), the interceptor records the file path.
/// When VS later sends <c>textDocument/didOpen</c> for that same file (Send direction), the
/// interceptor calls <see cref="VsProjectEventMonitor.SendScaffoldedFileAsync"/> to inject a
/// <c>reqnroll/projectFiles</c> delta into the server's stdin before forwarding the
/// notification.  This guarantees the server knows the file belongs to the project before it
/// runs Roslyn binding discovery, preventing the I2 exclusion.
/// </para>
/// </summary>
internal sealed class ScaffoldTrackingInterceptor : ILspMessageInterceptor
{
    private readonly Func<VsProjectEventMonitor?> _getMonitor;
    private readonly ILogger<ScaffoldTrackingInterceptor> _logger;
    private readonly ConcurrentDictionary<string, byte> _pendingFiles
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the interceptor over a deferred accessor for the (not-yet-constructed) project event monitor.</summary>
    public ScaffoldTrackingInterceptor(
        Func<VsProjectEventMonitor?> getMonitor,
        ILogger<ScaffoldTrackingInterceptor> logger)
    {
        _getMonitor = getMonitor ?? throw new ArgumentNullException(nameof(getMonitor));
        _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LspInterceptorResult> InterceptAsync(
        LspMessage        message,
        CancellationToken cancellationToken)
    {
        if (message.Direction == LspMessageDirection.Receive)
        {
            TrackScaffoldedFilesFromCodeActionResponse(message);
        }
        else
        {
            await MaybeInjectMembershipDeltaAsync(message, cancellationToken)
                .ConfigureAwait(false);
        }
        return LspInterceptorResult.PassThrough;
    }

    // ── Receive path: detect CreateFile .cs entries in code action responses ─────

    private void TrackScaffoldedFilesFromCodeActionResponse(LspMessage message)
    {
        if (!message.IsResponse)
            return;

        var result = message.Body["result"] as JArray;
        if (result is null)
            return;

        foreach (var item in result)
        {
            var changes = item?["edit"]?["documentChanges"] as JArray;
            if (changes is null)
                continue;

            foreach (var change in changes)
            {
                if (change?["kind"]?.Value<string>() != "create")
                    continue;

                var uri = change["uri"]?.Value<string>();
                if (uri is null || !uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = UriToPath(uri);
                if (path is null)
                    continue;

                _pendingFiles.TryAdd(path, 0);
                _logger.LogDebug(
                    "ScaffoldTrackingInterceptor: tracking scaffolded file {FileName}",
                    Path.GetFileName(path));
            }
        }
    }

    // ── Send path: inject membership delta before textDocument/didOpen ────────────

    private async Task MaybeInjectMembershipDeltaAsync(
        LspMessage        message,
        CancellationToken ct)
    {
        if (message.Method != "textDocument/didOpen")
            return;

        var uri = message.Body["params"]?["textDocument"]?["uri"]?.Value<string>();
        if (uri is null)
            return;

        var path = UriToPath(uri);
        if (path is null || !_pendingFiles.TryRemove(path, out _))
            return;

        var monitor = _getMonitor();
        if (monitor is null)
        {
            _logger.LogWarning(
                "ScaffoldTrackingInterceptor: VsProjectEventMonitor not available for {FileName}",
                Path.GetFileName(path));
            return;
        }

        _logger.LogDebug(
            "ScaffoldTrackingInterceptor: injecting projectFiles delta before didOpen for {FileName}",
            Path.GetFileName(path));

        await monitor.SendScaffoldedFileAsync(path, ct).ConfigureAwait(false);
    }

    private static string? UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "file")
            return null;
        return parsed.LocalPath;
    }
}
