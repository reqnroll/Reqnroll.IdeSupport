using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>
/// Thread-safe <see cref="IDocumentGenerations"/>. Entries are created on first bump and kept for
/// the life of the process, including after a document closes: keeping them is what lets a result
/// computed before the close be recognised as older than one computed after it.
/// </summary>
public sealed class DocumentGenerations : IDocumentGenerations
{
    // Case-insensitive like DocumentBufferService, so both agree on which URIs are the same document.
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public long Current(string documentId)
        => _generations.TryGetValue(documentId, out var generation) ? generation : 0;

    /// <inheritdoc/>
    // AddOrUpdate may run the update function more than once under contention, but only one result
    // is stored per attempt and each attempt reads the latest value, so concurrent bumps never lose
    // an increment.
    public long Bump(string documentId)
        => _generations.AddOrUpdate(documentId, 1, (_, generation) => generation + 1);
}
