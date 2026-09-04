using Reqnroll.IdeSupport.LSP.Server.Pipeline;

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
/// with stale or incomplete diagnostics that nothing subsequently retries.
///
/// The actual "reparse, then publish only if still open" work is
/// <see cref="IFeatureDocumentReparser.ReparseIfOpenAsync"/> (issue #578) — safe to call for a
/// document the server does not know is open, since the VS-side activation signal can race ahead
/// of <c>didOpen</c> (see #85 design discussion). Reusing it means diagnostics, semantic tokens,
/// and any future subscriber of the resulting <c>MatchCacheChangedNotification</c> all get
/// refreshed for free, with no separate republish logic to maintain here.
///
/// Routes through <see cref="IParseCoordinator"/> (issue #576) rather than awaiting inline.
/// Without this, a tab activation racing a <c>didChange</c> for the same URI could run two
/// concurrent parses against the same document (the #554 shape), and
/// <c>FoldingRangeHandler</c>/<c>DocumentSymbolHandler</c>'s <c>WaitForReadyAsync</c> guard —
/// their only defence against reading a stale buffer, since neither has an LSP refresh
/// capability to self-heal — would see no pending entry for an activation-triggered reparse.
/// </remarks>
public sealed class DocumentActivatedHandler
{
    private readonly IFeatureDocumentReparser _reparser;
    private readonly IParseCoordinator        _parseCoordinator;

    /// <summary>Initializes a new instance of the <see cref="DocumentActivatedHandler"/> class.</summary>
    public DocumentActivatedHandler(
        IFeatureDocumentReparser reparser,
        IParseCoordinator        parseCoordinator)
    {
        _reparser         = reparser;
        _parseCoordinator = parseCoordinator;
    }

    /// <summary>Handles a <c>reqnroll/documentActivated</c> notification by scheduling a match recompute.</summary>
    public Task HandleAsync(DocumentActivatedParams request, CancellationToken cancellationToken)
    {
        var uri = request.Uri;

        _parseCoordinator.Schedule(uri, ct => _reparser.ReparseIfOpenAsync(uri, ct));
        return Task.CompletedTask;
    }
}
