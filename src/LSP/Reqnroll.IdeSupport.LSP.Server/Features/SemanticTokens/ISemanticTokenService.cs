using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Maintains a per-document cache of LSP semantic tokens encoded from Gherkin
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.DeveroomTag"/> instances.
/// Encoding is deferred until <see cref="GetSemanticTokensAsync"/> is called; tags are
/// read directly from <see cref="IDocumentBufferService"/>.
/// </summary>
public interface ISemanticTokenService
{
    /// <summary>The shared legend that must be returned by the server's initialize response.</summary>
    SemanticTokensLegend Legend { get; }

    /// <summary>
    /// Returns the cached encoded token data for the requested document version,
    /// or <see langword="null"/> when no data is available yet.
    /// </summary>
    Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensAsync(DocumentUri uri, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns semantic tokens for only the given <paramref name="range"/>, encoded fresh from the
    /// current tags on every call (not cached — a range result is a subset of the full-document
    /// cache entry <see cref="GetSemanticTokensAsync"/> maintains, and caching every distinct
    /// viewport range would bloat that cache for no benefit; encoding is now proportional to the
    /// range's tag count instead of the whole document, so recomputing per call is cheap).
    /// Backs <c>textDocument/semanticTokens/range</c> — issue #471: this used to compute and
    /// discard the entire document.
    /// </summary>
    Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensForRangeAsync(
        DocumentUri uri, int version,
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts any cached token result for <paramref name="uri"/>, forcing the next
    /// <see cref="GetSemanticTokensAsync"/> call to re-encode from the current tags.
    /// Call this whenever the document's tags are updated without a version bump
    /// (e.g. after binding discovery completes for an already-open file).
    /// </summary>
    void InvalidateCache(DocumentUri uri);
}
