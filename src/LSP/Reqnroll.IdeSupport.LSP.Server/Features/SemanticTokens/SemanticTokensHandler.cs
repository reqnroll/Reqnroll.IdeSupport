using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using LspSemanticTokens = OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens;
using LspSemanticTokensFullOrDelta = OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokensFullOrDelta;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Handles <c>textDocument/semanticTokens/full</c>, <c>textDocument/semanticTokens/full/delta</c>,
/// and <c>textDocument/semanticTokens/range</c> requests by delegating to <see cref="ISemanticTokensService"/>.
/// </summary>
public class SemanticTokensHandler
{
    // OmniSharp's DelegatingRequestHandler serialises the response with JToken.FromObject(),
    // which throws ArgumentNullException when passed null — even though LSP allows null.
    // Return this instead of null whenever the service has no tokens yet.
    private static readonly LspSemanticTokens EmptyTokens = new() { Data = [] };

    private readonly ISemanticTokensService _semanticTokenService;
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="SemanticTokensHandler"/> class.</summary>
    public SemanticTokensHandler(
        ISemanticTokensService semanticTokenService,
        IDocumentBufferService documentBufferService,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _semanticTokenService = semanticTokenService;
        _documentBufferService = documentBufferService;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    // ── Full ──────────────────────────────────────────────────────────────────

    /// <summary>Handles a semantic-tokens request (<c>textDocument/semanticTokens/full</c>, <c>/full/delta</c>, <c>/range</c>).</summary>
    public async Task<LspSemanticTokens> HandleAsync(
        SemanticTokensParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): close the gap between the asserted 100ms synthetic
        // target and real-world field data — this operation had none before issue #113.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentSemanticTokensFull, uri);

        if (!IsFeatureFile(uri)) return EmptyTokens;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/full requested for {uri} (version {version})");

        return await _semanticTokenService.GetSemanticTokensAsync(uri, version, cancellationToken)
                                          .ConfigureAwait(false)
               ?? EmptyTokens;
    }

    // ── Delta ─────────────────────────────────────────────────────────────────
    // We don't maintain delta state; return the full token set wrapped in SemanticTokensFullOrDelta.

    /// <summary>Handles a <c>textDocument/semanticTokens/full/delta</c> request.</summary>
    public async Task<LspSemanticTokensFullOrDelta> HandleAsync(
        SemanticTokensDeltaParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentSemanticTokensFullDelta, uri);

        if (!IsFeatureFile(uri)) return new LspSemanticTokensFullOrDelta(EmptyTokens);
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/full/delta requested for {uri} (version {version}), returning full tokens");

        var tokens = await _semanticTokenService.GetSemanticTokensAsync(uri, version, cancellationToken)
                                                .ConfigureAwait(false);

        return new LspSemanticTokensFullOrDelta(tokens ?? EmptyTokens);
    }

    // ── Range ─────────────────────────────────────────────────────────────────

    /// <summary>Handles a <c>textDocument/semanticTokens/range</c> request.</summary>
    public async Task<LspSemanticTokens> HandleAsync(
        SemanticTokensRangeParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentSemanticTokensRange, uri);

        if (!IsFeatureFile(uri)) return EmptyTokens;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/range requested for {uri} (version {version})");

        return await _semanticTokenService
            .GetSemanticTokensForRangeAsync(uri, version, request.Range, cancellationToken)
            .ConfigureAwait(false)
            ?? EmptyTokens;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsFeatureFile(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri)
        => uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    private int GetCurrentVersion(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri)
    {
        if (_documentBufferService.TryGet(uri, out var buffer) && buffer?.Version is int v)
            return v;

        return 0;
    }
}
