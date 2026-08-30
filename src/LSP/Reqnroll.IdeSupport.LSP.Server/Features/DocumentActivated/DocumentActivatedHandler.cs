using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;

/// <summary>
/// Handles <c>reqnroll/documentActivated</c> (issue #85): forces a fresh binding-match
/// recompute and diagnostics/semantic-tokens republish for a document the VS extension has
/// just detected becoming the active tab, independent of whatever normally triggers that
/// (didOpen/didChange, a binding-registry replacement, a config change).
/// </summary>
/// <remarks>
/// Exists as a backstop for the #78 class of startup race: several <c>.feature</c> documents
/// can be opened back-to-back before the server has finished discovery, leaving some of them
/// with stale or incomplete diagnostics that nothing subsequently retries. Reusing
/// <see cref="IGherkinDocumentTaggerService.ParseAsync"/> + <see cref="MatchCacheChangedNotification"/>
/// — the exact same pipeline <see cref="TextDocumentSyncHandler"/> uses for didOpen/didChange —
/// means diagnostics, semantic tokens, and any future subscriber of the notification all get
/// refreshed for free, with no separate republish logic to maintain here.
///
/// Safe to call for a document the server does not know is open: <c>ParseAsync</c> no-ops when
/// there is no buffer for the URI, and the notification then carries a synthetic version of 0 —
/// this deliberately degrades to nothing happening rather than throwing, since the VS-side
/// activation signal can race ahead of <c>didOpen</c> (see #85 design discussion).
/// </remarks>
public sealed class DocumentActivatedHandler
{
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IMediator                     _mediator;
    private readonly IIdeSupportLogger                _logger;

    /// <summary>Initializes a new instance of the <see cref="DocumentActivatedHandler"/> class.</summary>
    public DocumentActivatedHandler(
        IGherkinDocumentTaggerService taggerService,
        IDocumentBufferService        documentBufferService,
        IMediator                     mediator,
        IIdeSupportLogger                logger)
    {
        _taggerService         = taggerService;
        _documentBufferService = documentBufferService;
        _mediator              = mediator;
        _logger                = logger;
    }

    /// <summary>Handles a <c>reqnroll/documentActivated</c> notification to force a match recompute.</summary>
    public async Task HandleAsync(DocumentActivatedParams request, CancellationToken cancellationToken)
    {
        var uri = request.Uri;

        // version: null — the client only knows the URI became active, not the document
        // version it currently holds; ParseAsync reads whatever version the open buffer has.
        await _taggerService.ParseAsync(uri, version: null).ConfigureAwait(false);

        if (!_documentBufferService.TryGet(uri, out var buffer))
        {
            _logger.LogVerbose($"DocumentActivatedHandler: no open buffer for '{uri}' — nothing to republish.");
            return;
        }

        _logger.LogInfo($"DocumentActivatedHandler: recomputed and republishing for '{uri}'");
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, buffer?.Version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }
}
