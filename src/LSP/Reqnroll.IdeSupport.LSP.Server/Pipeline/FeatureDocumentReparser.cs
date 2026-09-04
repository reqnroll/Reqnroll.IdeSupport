using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="IFeatureDocumentReparser"/>
public sealed class FeatureDocumentReparser : IFeatureDocumentReparser
{
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IMediator _mediator;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="FeatureDocumentReparser"/> class.</summary>
    public FeatureDocumentReparser(
        IGherkinDocumentTaggerService taggerService,
        IDocumentBufferService documentBufferService,
        IMediator mediator,
        IIdeSupportLogger logger)
    {
        _taggerService = taggerService;
        _documentBufferService = documentBufferService;
        _mediator = mediator;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ReparseOpenDocumentAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        // ParseAsync stores updated tags, recomputes/stores the binding match set, and
        // invalidates the semantic token cache internally before this notification fires.
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReparseIfOpenAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        // version: null — the caller only knows the URI, not the document version it currently
        // holds (if any); ParseAsync reads whatever version the open buffer has.
        await _taggerService.ParseAsync(uri, version: null).ConfigureAwait(false);

        if (!_documentBufferService.TryGet(uri, out var buffer))
        {
            _logger.LogVerbose($"FeatureDocumentReparser: no open buffer for '{uri}' — nothing to republish.");
            return;
        }

        _logger.LogInfo($"FeatureDocumentReparser: recomputed and republishing for '{uri}'.");
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, buffer?.Version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }
}
