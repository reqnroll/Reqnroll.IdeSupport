using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

/// <summary>
/// Observes <c>textDocument/semanticTokens</c> traffic flowing through the LSP connection and
/// records the decoded tokens (and the legend) into <see cref="SemanticTokenClassificationStore"/>,
/// so the editor classifier can colour <c>.feature</c> files with Reqnroll's custom classifications.
/// </summary>
/// <remarks>
/// The same instance is registered on both the send (VS→Server) and receive (Server→VS) interceptor
/// lists: it learns the request→document mapping from outgoing requests and decodes the matching
/// responses.  It never consumes a message — everything is passed through untouched so VS's own
/// pipeline keeps working.
/// </remarks>
internal sealed class SemanticTokensClassificationInterceptor : ILspMessageInterceptor
{
    private readonly SemanticTokenClassificationStore _store;
    private readonly ILogger<SemanticTokensClassificationInterceptor> _logger;

    // Outstanding semanticTokens request id → normalized file key.
    private readonly ConcurrentDictionary<string, string> _pendingByRequestId =
        new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates the interceptor with the shared token store and logger.</summary>
    public SemanticTokensClassificationInterceptor(
        SemanticTokenClassificationStore store, ILogger<SemanticTokensClassificationInterceptor> logger)
    {
        _store  = store  ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Intercepts an LSP message, capturing semantic-token data from requests, responses, and push notifications.</summary>
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (message.IsNotification && message.Method == "reqnroll/semanticTokens")
            {
                // Primary path: the server proactively pushes tokens for the VS client (which does
                // not reliably pull them). The notification is passed through; VS ignores it.
                CapturePushedTokens(message);
            }
            else if (message.IsRequest && IsSemanticTokensMethod(message.Method))
            {
                var uri = message.Body["params"]?["textDocument"]?["uri"]?.Value<string>();
                var key = SemanticTokenClassificationStore.NormalizeKey(uri);
                if (key is not null && message.Id is not null)
                    _pendingByRequestId[message.Id.ToString()] = key;
            }
            else if (message.IsResponse)
            {
                // Fallback path: if VS does pull tokens, capture those responses too.
                CaptureLegendIfPresent(message);
                CaptureTokensIfMatched(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SemanticTokensClassificationInterceptor: failed to process message.");
        }

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    private void CapturePushedTokens(LspMessage message)
    {
        var uri = message.Body["params"]?["uri"]?.Value<string>();
        var fileKey = SemanticTokenClassificationStore.NormalizeKey(uri);
        if (fileKey is null) return;
        if (message.Body["params"]?["data"] is not JArray data) return;

        var tokens = Decode(data, _store.Legend);
        _store.SetTokens(fileKey, tokens);
        _logger.LogDebug(
            "SemanticTokensClassificationInterceptor: stored {TokenCount} pushed tokens for {FileKey}.", tokens.Count, fileKey);
    }

    private void CaptureLegendIfPresent(LspMessage message)
    {
        // The initialize response carries the server's semantic-token legend. Every other response's
        // "result" is shaped differently (JArray, scalar JValue, etc.) and must be skipped without
        // indexing into it, since only JObject indexers tolerate a missing key.
        if (message.Body["result"] is not JObject result) return;
        var tokenTypes = result["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenTypes"] as JArray;
        if (tokenTypes is null) return;

        _store.SetLegend(tokenTypes.Select(t => t.Value<string>() ?? string.Empty).ToArray());
        _logger.LogDebug(
            "SemanticTokensClassificationInterceptor: captured legend ({TokenTypeCount} token types).", tokenTypes.Count);
    }

    private void CaptureTokensIfMatched(LspMessage message)
    {
        if (message.Id is null) return;
        if (!_pendingByRequestId.TryRemove(message.Id.ToString(), out var fileKey)) return;

        // Both semanticTokens/full and the full form of semanticTokens/full/delta carry a flat
        // "data" int array. (Edit-style delta results carry "edits" instead — skipped here; the
        // next full response will refresh.) "result" itself may be a non-JObject JValue, which
        // must be checked before indexing since only JObject indexers tolerate a missing key.
        if (message.Body["result"] is not JObject result) return;
        if (result["data"] is not JArray data) return;

        var tokens = Decode(data, _store.Legend);
        _store.SetTokens(fileKey, tokens);
        _logger.LogDebug(
            "SemanticTokensClassificationInterceptor: stored {TokenCount} tokens for {FileKey}.", tokens.Count, fileKey);
    }

    private static bool IsSemanticTokensMethod(string? method) =>
        method is "textDocument/semanticTokens/full"
               or "textDocument/semanticTokens/full/delta"
               or "textDocument/semanticTokens/range";

    /// <summary>Decodes the LSP 5-int relative encoding into absolute <see cref="ClassifiedToken"/>s.</summary>
    private static IReadOnlyList<ClassifiedToken> Decode(JArray data, string[] legend)
    {
        var list = new List<ClassifiedToken>(data.Count / 5);
        int line = 0, ch = 0;

        for (int i = 0; i + 4 < data.Count; i += 5)
        {
            int deltaLine = data[i].Value<int>();
            int deltaChar = data[i + 1].Value<int>();
            int length = data[i + 2].Value<int>();
            int typeIndex = data[i + 3].Value<int>();

            if (deltaLine == 0) ch += deltaChar;
            else { line += deltaLine; ch = deltaChar; }

            if (length <= 0) continue;
            if (typeIndex < 0 || typeIndex >= legend.Length) continue;

            list.Add(new ClassifiedToken(line, ch, length, legend[typeIndex]));
        }

        return list;
    }
}
