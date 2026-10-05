#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.NavigationBar;

/// <summary>
/// Fetches the Feature/Scenario/Step symbol tree for the Navigation Bar
/// by sending the custom <c>reqnroll/documentSymbolHierarchical</c> request over the owned
/// <see cref="LspInterceptingPipe"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Navigation Bar design (Issue #5):</b> the VS Navigation Bar drop-downs are populated from
/// the LSP server's document-symbol data rather than from a VS-side Gherkin parser. This service
/// is the client-side half of that design: it fetches the symbol tree from the server and maps it
/// into the protocol-agnostic <see cref="GherkinSymbolNode"/> shape that the VSSDK-hosted
/// <c>GherkinDropdownBarClient</c> renders, so the combo boxes always reflect what the language
/// server itself understands about the document (same source of truth as diagnostics, folding,
/// etc.) instead of a second, potentially-diverging parser living in the VS integration layer.
/// </para>
/// <para>
/// Deliberately not <c>textDocument/documentSymbol</c>: that handler's response shape depends on
/// whether the real LSP client (VS) declared <c>hierarchicalDocumentSymbolSupport</c> — which VS
/// does not — so it can return either nested <c>DocumentSymbol</c> or flat <c>SymbolInformation</c>
/// depending on that capability, per-handler-instance rather than per-request. This service's
/// <see cref="MapResult"/> is written for the nested shape (reads <c>range</c>/<c>selectionRange</c>/
/// <c>children</c> directly), so it uses the always-hierarchical custom method instead of being at
/// the mercy of what VS's client capability happens to be.
/// </para>
/// </remarks>
internal sealed class GherkinNavigationBarSymbolService
{
    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<GherkinNavigationBarSymbolService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public GherkinNavigationBarSymbolService(LspInterceptingPipe pipe, ILogger<GherkinNavigationBarSymbolService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for the Feature/Scenario/Step symbol tree of the given document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Retries once on <c>ContentModified</c></b> (issue #671, R6a). This request is dispatched
    /// on OmniSharp's default Parallel lane, and its scheduler cancels <i>every</i> outstanding
    /// Parallel request — with no per-document scoping — the moment any Serial-dispatched
    /// notification (<c>didChange</c>/<c>didOpen</c>/<c>didSave</c>) arrives (the same mechanism
    /// documented for issue #654's rename failures). Unlike rename, this is not a rare occurrence:
    /// a <c>didChange</c> fires on every keystroke, so this request racing ordinary typing is the
    /// common case, not the exception.
    /// </para>
    /// <para>
    /// The result is not merely a stale-data cosmetic issue here. This service is the sole source
    /// of scenario ranges for <see cref="RunTestCodeLens.RunTestCodeLensService.GetTargetsForLineAsync"/>
    /// — a swallowed <c>ContentModified</c> previously came back as an empty symbol list
    /// indistinguishable from "no scenario found," which meant the Run CodeLens for that scenario
    /// silently failed to render, with nothing to tell it apart from a genuine miss.
    /// </para>
    /// <para>
    /// One retry, not a loop: by the time the retry is sent, the notification that caused the
    /// cancellation has already been processed (it was Serial-dispatched and this call awaits the
    /// first attempt's full round trip first), so the retry is racing only whatever the user typed
    /// in that gap — a bounded, one-shot mitigation rather than a retry storm under sustained
    /// typing. A second collision throws <see cref="LspContentModifiedException"/> rather than
    /// returning no symbols, so no caller mistakes it for a real empty answer (issue #800
    /// follow-up; seen live at startup, when the extension's own project notifications collided
    /// twice). The Run CodeLens cache restarts its computation, and the Navigation Bar schedules
    /// another refresh.
    /// </para>
    /// <para>
    /// The proper fix — not retrying around a scheduling defect — is OmniSharp's global,
    /// unscoped Parallel cancellation itself; see #671 for the broader design discussion. This is
    /// the client-side mitigation available without changing that scheduling behavior.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<GherkinSymbolNode>> FetchSymbolsAsync(
        string fileUri, CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri);

        _logger.LogDebug(
            "GherkinNavigationBarSymbolService: querying {RequestMethod} for {FileUri}", CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, fileUri);

        var (result, error) = await SendWithContentModifiedRetryAsync(paramsJson, fileUri, cancellationToken)
            .ConfigureAwait(false);

        // A cancelled request comes back from the pipe as an empty result, not an exception. Mapping
        // that to "no symbols" made the Run CodeLens cache a genuine-looking empty answer for a
        // scenario line when its computation was cancelled by an invalidation (issue #78): the
        // lens then stayed blank for the session. Cancellation must surface as cancellation.
        cancellationToken.ThrowIfCancellationRequested();

        // The same failure mode for ContentModified that survived the one retry: "no symbols"
        // would be cached as a real answer (issue #800 follow-up). Callers treat this exception
        // as "ask again later".
        if (IsContentModified(error))
            throw new LspContentModifiedException(CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, fileUri);

        if (error != null)
        {
            _logger.LogDebug(
                "GherkinNavigationBarSymbolService: {RequestMethod} for {FileUri} returned error {Error}; treating as no symbols.",
                CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, fileUri, error);
        }

        var mapped = MapResult(result as JArray);

        _logger.LogDebug(
            "GherkinNavigationBarSymbolService: uri={FileUri} mapped {SymbolCount} top-level symbol(s).",
            fileUri, mapped.Count);

        return mapped;
    }

    private async Task<(JToken? Result, JObject? Error)> SendWithContentModifiedRetryAsync(
        string paramsJson, string fileUri, CancellationToken cancellationToken)
    {
        var attempt = await _pipe
            .SendRequestToServerWithErrorAsync(CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        if (!IsContentModified(attempt.Error))
            return attempt;

        _logger.LogDebug(
            "GherkinNavigationBarSymbolService: {RequestMethod} for {FileUri} was cancelled with ContentModified " +
            "(a concurrent edit raced this request); retrying once.", CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, fileUri);

        return await _pipe
            .SendRequestToServerWithErrorAsync(CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, paramsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><see langword="internal"/> so the retry decision is unit-testable without a live <see cref="LspInterceptingPipe"/>.</summary>
    internal static bool IsContentModified(JObject? error) =>
        error?["code"]?.Value<int>() == LspContentModifiedException.ErrorCode;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri) => LspParamsBuilder.TextDocumentUri(fileUri);

    /// <summary>Pure mapping from a raw <c>reqnroll/documentSymbolHierarchical</c> JSON array to a list of <see cref="GherkinSymbolNode"/>. Separated from transport so it can be unit-tested.</summary>
    internal static IReadOnlyList<GherkinSymbolNode> MapResult(JArray? array)
    {
        if (array is null || array.Count == 0)
            return Array.Empty<GherkinSymbolNode>();

        var result = new List<GherkinSymbolNode>(array.Count);
        foreach (var item in array)
        {
            if (item is JObject obj)
                result.Add(MapNode(obj));
        }
        return result;
    }

    private static GherkinSymbolNode MapNode(JObject obj)
    {
        var name = obj["name"]?.Value<string>() ?? string.Empty;
        var kind = obj["kind"]?.Value<int>() ?? 0;
        var detail = obj["detail"]?.Value<string>();
        var range = MapRange(obj["range"] as JObject);
        var selectionRange = MapRange(obj["selectionRange"] as JObject);
        var children = MapResult(obj["children"] as JArray);

        return new GherkinSymbolNode(name, kind, range, selectionRange, children, detail);
    }

    private static GherkinSymbolRange MapRange(JObject? range)
    {
        if (range is null)
            return default;

        return new GherkinSymbolRange(
            MapPosition(range["start"] as JObject),
            MapPosition(range["end"] as JObject));
    }

    private static GherkinSymbolPosition MapPosition(JObject? position)
    {
        if (position is null)
            return default;

        return new GherkinSymbolPosition(
            position["line"]?.Value<int>() ?? 0,
            position["character"]?.Value<int>() ?? 0);
    }
}
