#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio; // GherkinLineRangeEdit

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FormatDocument;

/// <summary>
/// Sends <c>textDocument/formatting</c> (whole document) or <c>textDocument/rangeFormatting</c>
/// (selection) requests to the LSP server for Format Document / Format Selection.
/// </summary>
/// <remarks>
/// Unlike Comment/Uncomment and Rename, formatting is a plain LSP request/response — the server
/// does not push a <c>workspace/applyEdit</c> for it, so the caller (<c>FormatDocumentCommandFilter</c>)
/// is responsible for applying the returned edit(s) to the VS text buffer itself.
/// </remarks>
internal sealed class FormatDocumentService
{
    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<FormatDocumentService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public FormatDocumentService(LspInterceptingPipe pipe, ILogger<FormatDocumentService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Sends <c>textDocument/formatting</c> (when <paramref name="isSelection"/> is false) or
    /// <c>textDocument/rangeFormatting</c> for lines [<paramref name="startLine"/>..<paramref name="endLine"/>]
    /// (0-based, inclusive) and maps the response to line-range edits.
    /// </summary>
    public async Task<IReadOnlyList<GherkinLineRangeEdit>?> FormatDocumentAsync(
        string            fileUri,
        bool              isSelection,
        int               startLine,
        int               endLine,
        CancellationToken cancellationToken)
    {
        var method     = isSelection ? "textDocument/rangeFormatting" : "textDocument/formatting";
        var paramsJson = BuildParams(fileUri, isSelection, startLine, endLine);

        _logger.LogDebug(
            "FormatDocumentService: sending {Method} uri={FileUri} isSelection={IsSelection}",
            method, fileUri, isSelection);
        _logger.LogTrace(
            "FormatDocumentService: sending {Method} params={ParamsJson}", method, paramsJson);

        var result = await _pipe
            .SendRequestToServerAsync(method, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogTrace(
            "FormatDocumentService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        var edits = MapResult(result);
        _logger.LogDebug("FormatDocumentService: {EditCount} edit(s) returned", edits.Count);
        return edits;
    }

    /// <summary>
    /// Pure mapping from a raw <c>textDocument/formatting</c>/<c>rangeFormatting</c> JSON result
    /// (an array of LSP <c>TextEdit</c>) to <see cref="GherkinLineRangeEdit"/>s. Separated from
    /// transport so it can be unit-tested. A <c>null</c> or non-array result yields no edits.
    /// </summary>
    internal static IReadOnlyList<GherkinLineRangeEdit> MapResult(JToken? result)
    {
        if (result is not JArray array)
            return Array.Empty<GherkinLineRangeEdit>();

        var edits = new List<GherkinLineRangeEdit>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var range     = obj["range"] as JObject;
            var startLine = range?["start"]?["line"]?.Value<int>();
            var endLine   = range?["end"]?["line"]?.Value<int>();
            var newText   = obj["newText"]?.Value<string>();

            if (startLine is null || endLine is null || newText is null) continue;

            edits.Add(new GherkinLineRangeEdit(startLine.Value, endLine.Value, newText));
        }
        return edits;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, bool isSelection, int startLine, int endLine)
    {
        var builder = new LspParamsBuilder()
            .AddTextDocument(fileUri)
            .AddRaw("options", "{\"tabSize\":4,\"insertSpaces\":true}");

        if (isSelection)
        {
            builder.AddRaw(
                "range",
                $"{{\"start\":{{\"line\":{startLine},\"character\":0}},\"end\":{{\"line\":{endLine},\"character\":0}}}}");
        }

        return builder.Build();
    }
}
