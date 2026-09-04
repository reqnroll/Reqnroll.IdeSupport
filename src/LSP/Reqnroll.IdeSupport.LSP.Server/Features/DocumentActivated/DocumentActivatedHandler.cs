using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
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
///
/// Routes the reparse through <see cref="IParseCoordinator"/> (issue #576) rather than awaiting
/// it inline, matching every other open-document reparse path. Without this, a tab activation
/// racing a <c>didChange</c> for the same URI could run two concurrent <c>ParseAsync</c> calls
/// against the same document (the #554 shape), and <c>FoldingRangeHandler</c>/
/// <c>DocumentSymbolHandler</c>'s <c>WaitForReadyAsync</c> guard — their only defence against
/// reading a stale buffer, since neither has an LSP refresh capability to self-heal — would see
/// no pending entry for an activation-triggered reparse.
/// </remarks>
public sealed class DocumentActivatedHandler
{
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IMediator                     _mediator;
    private readonly IParseCoordinator             _parseCoordinator;
    private readonly IIdeSupportLogger                _logger;

    /// <summary>Initializes a new instance of the <see cref="DocumentActivatedHandler"/> class.</summary>
    public DocumentActivatedHandler(
        IGherkinDocumentTaggerService taggerService,
        IDocumentBufferService        documentBufferService,
        IMediator                     mediator,
        IParseCoordinator             parseCoordinator,
        IIdeSupportLogger                logger)
    {
        _taggerService         = taggerService;
        _documentBufferService = documentBufferService;
        _mediator              = mediator;
        _parseCoordinator      = parseCoordinator;
        _logger                = logger;
    }

    /// <summary>Handles a <c>reqnroll/documentActivated</c> notification by scheduling a match recompute.</summary>
    public Task HandleAsync(DocumentActivatedParams request, CancellationToken cancellationToken)
    {
        var uri = request.Uri;

        _parseCoordinator.Schedule(uri, ct => ParseAndNotifyAsync(uri, cancellationToken: ct));
        return Task.CompletedTask;
    }

    private async Task ParseAndNotifyAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
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
