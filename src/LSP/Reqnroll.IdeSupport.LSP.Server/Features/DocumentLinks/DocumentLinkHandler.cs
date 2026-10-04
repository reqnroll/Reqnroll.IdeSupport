using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Parsing;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.DocumentLinks;

/// <summary>
/// Handles <c>textDocument/documentLink</c> for <c>.feature</c> files: turns Gherkin tags that match a
/// <c>traceability/tagLinks</c> pattern (e.g. <c>@issue:1234</c>) into clickable links.
/// </summary>
/// <remarks>
/// Registered manually with <c>documentLinkProvider</c> declared statically in the initialize response
/// (see <c>Program.ConfigureServer</c>), for the same dynamic-registration-race reason as
/// <see cref="Features.Folding.FoldingRangeHandler"/>. Links carry their target eagerly, so no
/// <c>documentLink/resolve</c> support is advertised.
/// </remarks>
public sealed class DocumentLinkHandler
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IParseCoordinator _parseCoordinator;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="DocumentLinkHandler"/> class.</summary>
    public DocumentLinkHandler(
        IDocumentBufferService documentBufferService,
        ILspWorkspaceScopeManager scopeManager,
        IParseCoordinator parseCoordinator,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _scopeManager = scopeManager;
        _parseCoordinator = parseCoordinator;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>textDocument/documentLink</c> request, returning one link per configured, matching tag.</summary>
    public async Task<DocumentLinkContainer?> HandleAsync(DocumentLinkParams request, CancellationToken ct)
    {
        using var _perf = _recorder.Measure(LspStandardMethodNames.TextDocumentDocumentLink, request.TextDocument.Uri);

        _logger.LogInfo($"Tag links textDocument/documentLink: {request.TextDocument.Uri}");

        try
        {
            return await ResolveLinksAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request answers null on the wire, which a client treats as "no links" and (VS Code)
            // does not retry - so leave a trace of it.
            _logger.LogVerbose($"Tag links: request for {request.TextDocument.Uri} was cancelled (ct.IsCancellationRequested={ct.IsCancellationRequested}).");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Tag links: request for {request.TextDocument.Uri} failed: {ex.Message}");
            throw;
        }
    }

    private async Task<DocumentLinkContainer?> ResolveLinksAsync(DocumentLinkParams request, CancellationToken ct)
    {

        // documentLink has no refresh mechanism, so wait for any in-flight parse (see FoldingRangeHandler).
        await _parseCoordinator.WaitForReadyAsync(request.TextDocument.Uri, ct).ConfigureAwait(false);

        if (!_documentBufferService.TryGet(request.TextDocument.Uri, out var buffer) || buffer?.Tags is null)
        {
            _logger.LogVerbose($"Tag links: no parsed buffer for {request.TextDocument.Uri}; returning no links.");
            return new DocumentLinkContainer();
        }

        var traceability = _scopeManager.GetConfigurationProviderForUri(request.TextDocument.Uri)
            .GetConfiguration()?.Traceability;
        if (traceability is null || traceability.TagLinks.Length == 0)
        {
            _logger.LogVerbose($"Tag links: no tag links configured for {request.TextDocument.Uri}; returning no links.");
            return new DocumentLinkContainer();
        }

        var links = new List<DocumentLink>();
        foreach (var tag in buffer.Tags)
        {
            if (tag.Type != IdeSupportTagTypes.Tag || tag.Data is not Gherkin.Ast.Tag gherkinTag)
                continue;

            var target = traceability.ResolveTagLink(gherkinTag.Name);
            if (target is null)
                continue;

            links.Add(new DocumentLink
            {
                Range = tag.Range.ToLspRange(),
                Target = target.AbsoluteUri,
            });
        }

        _logger.LogVerbose($"Tag links: {links.Count} link(s) for {request.TextDocument.Uri}.");
        return new DocumentLinkContainer(links);
    }

}
