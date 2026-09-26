using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Drops <c>textDocument/didOpen</c>, <c>didChange</c> and <c>didClose</c> notifications for
/// documents that live under the system temp directory or in a path containing "copilot"
/// (issue #562).
/// </summary>
/// <remarks>
/// <para>
/// VS occasionally opens a second, shadow <c>ITextBuffer</c> over the same logical
/// <c>.feature</c> file — observed for Copilot's
/// <c>%TEMP%\CopilotBaseline\&lt;guid&gt;\~&lt;name&gt;.feature</c> comparison copy. Because it
/// picks up our content type, our language client attaches to it and our server serves it for
/// real (didOpen, semantic tokens, folding ranges, ...), giving VS's own LSP client two
/// server-tracked buffers for what it treats as one file. That is the only candidate identified
/// for VS's <c>SnapshotSpan.TranslateTo</c> "doesn't belong to the correct TextBuffer" exception
/// during completion commit, which silently commits the wrong item (issue #561 is the resulting
/// data loss).
/// </para>
/// <para>
/// We gain nothing from tracking a shadow copy of a file we already have open for real, so this
/// interceptor keeps our server from ever registering one: it runs ahead of
/// <see cref="DocumentActivationTrackingInterceptor"/> and any other send-side interceptor so
/// none of them see these messages either.
/// </para>
/// </remarks>
internal sealed class ShadowDocumentFilterInterceptor : ILspMessageInterceptor
{
    private readonly ILogger<ShadowDocumentFilterInterceptor> _logger;

    private static readonly string TempDirectory =
        Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public ShadowDocumentFilterInterceptor(ILogger<ShadowDocumentFilterInterceptor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose"))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var path = UriToLocalPath(message);
        if (path is null || !IsShadowDocumentPath(path))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        _logger.LogDebug(
            "ShadowDocumentFilterInterceptor: dropped {Method} for shadow document {FileName} (issue #562).",
            message.Method, Path.GetFileName(path));
        return Task.FromResult(LspInterceptorResult.Consume);
    }

    /// <summary>
    /// True when <paramref name="path"/> sits under the system temp directory or has a path
    /// segment containing "copilot" — a shadow/scratch copy we never want our server to track.
    /// </summary>
    internal static bool IsShadowDocumentPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (fullPath.StartsWith(TempDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(TempDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.IndexOf("copilot", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static string? UriToLocalPath(LspMessage message)
    {
        var uri = message.Body["params"]?["textDocument"]?["uri"]?.Value<string>();
        if (uri is null)
            return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "file")
            return null;
        return parsed.LocalPath;
    }
}
