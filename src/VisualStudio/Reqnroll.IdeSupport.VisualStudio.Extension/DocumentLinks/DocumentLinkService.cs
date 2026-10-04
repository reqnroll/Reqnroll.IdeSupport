#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.DocumentLinks;

/// <summary>
/// Requests <c>textDocument/documentLink</c> for a <c>.feature</c> file over the owned
/// <see cref="LspInterceptingPipe"/> (issue #755). VS's built-in LSP client does not consume the
/// capability, so the clickable-tag Ctrl+Click is wired through <see cref="TagLinkRedirect"/> instead.
/// </summary>
internal sealed class DocumentLinkService
{
    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<DocumentLinkService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public DocumentLinkService(LspInterceptingPipe pipe, ILogger<DocumentLinkService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>Returns the clickable tag links in <paramref name="fileUri"/>; empty when there are none.</summary>
    public async Task<IReadOnlyList<TagLinkEntry>> GetLinksAsync(string fileUri, CancellationToken cancellationToken)
    {
        var result = await _pipe
            .SendRequestToServerAsync(
                LspStandardMethodNames.TextDocumentDocumentLink, LspParamsBuilder.TextDocumentUri(fileUri), cancellationToken)
            .ConfigureAwait(false);

        var links = MapResult(result);
        _logger.LogDebug("DocumentLinkService: {LinkCount} link(s) returned for {FileUri}", links.Count, fileUri);
        return links;
    }

    /// <summary>
    /// Pure mapping from a raw <c>DocumentLink[]</c> result. Entries without a range or target are
    /// skipped; a <c>null</c> or non-array result yields no links.
    /// </summary>
    internal static IReadOnlyList<TagLinkEntry> MapResult(JToken? result)
    {
        if (result is not JArray array)
            return System.Array.Empty<TagLinkEntry>();

        var links = new List<TagLinkEntry>();
        foreach (var item in array)
        {
            var start  = item["range"]?["start"];
            var end    = item["range"]?["end"];
            var target = item["target"]?.Value<string>();
            if (start is null || end is null || string.IsNullOrEmpty(target))
                continue;

            links.Add(new TagLinkEntry(
                start["line"]?.Value<int>() ?? 0, start["character"]?.Value<int>() ?? 0,
                end["line"]?.Value<int>()   ?? 0, end["character"]?.Value<int>()   ?? 0,
                target!));
        }
        return links;
    }
}
