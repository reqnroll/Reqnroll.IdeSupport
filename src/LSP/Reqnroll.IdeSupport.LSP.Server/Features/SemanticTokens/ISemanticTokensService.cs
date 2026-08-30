using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Maintains a per-document cache of LSP semantic tokens encoded from Gherkin
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.DeveroomTag"/> instances.
/// Encoding is deferred until <see cref="GetSemanticTokensAsync"/> is called; tags are
/// read directly from <see cref="IDocumentBufferService"/>.
/// </summary>
public interface ISemanticTokensService
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
    /// viewport range would bloat that cache for no benefit).
    /// Backs <c>textDocument/semanticTokens/range</c> — issue #471: this used to compute and
    /// discard the entire document.
    /// <para>
    /// Scoping is applied as early as the tag list allows: out-of-range tags are dropped up front,
    /// so sorting, overlap resolution and delta encoding all scale with the range's tag count
    /// rather than the document's. The one part that still scales with the document is position
    /// resolution — each in-range tag's offsets are turned into (line, character) by a linear scan
    /// over the snapshot's lines — but that is now done exactly once per tag, shared between the
    /// range filter and the token collection. Replacing that scan with precomputed line-start
    /// offsets plus a binary search is a separate follow-up.
    /// </para>
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
