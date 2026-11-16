using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Maintains a per-document cache of LSP semantic tokens encoded from Gherkin
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.IdeSupportTag"/> instances.
/// Encoding is deferred until <see cref="GetSemanticTokensAsync"/> is called; tags are
/// read directly from <see cref="IDocumentBufferService"/>.
/// </summary>
public interface ISemanticTokensService
{
    /// <summary>The shared legend that must be returned by the server's initialize response.</summary>
    SemanticTokensLegend Legend { get; }

    /// <summary>
    /// Returns the encoded token data for the document's current tags, cached under the stamp of the
    /// inputs those tags were derived from, or an empty token set when the document has no tags yet.
    /// <paramref name="version"/> is the client version the request was made for; it is not part of
    /// the cache key.
    /// </summary>
    Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensAsync(DocumentUri uri, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns semantic tokens for only the given <paramref name="range"/>, encoded fresh from the
    /// current tags on every call (not cached — a range result is a subset of the full-document
    /// cache entry <see cref="GetSemanticTokensAsync"/> maintains, and caching every distinct
    /// viewport range would bloat that cache for no benefit).
    /// Backs <c>textDocument/semanticTokens/range</c>. Only the requested range is processed, not
    /// the entire document.
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
}
