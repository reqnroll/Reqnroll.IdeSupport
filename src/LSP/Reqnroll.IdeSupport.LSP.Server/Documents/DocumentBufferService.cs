using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Documents;

/// <summary>The in-memory snapshot of an open document's URI, version, text, and any cached Gherkin tags.</summary>
/// <param name="Generation">
/// The document's content generation (see <c>IDocumentGenerations</c>) when this text became the
/// buffer. Unlike <paramref name="Version"/>, which the client assigns and restarts on reopen, it
/// orders this text against every other text the server has seen for the document, open or closed.
/// </param>
public record DocumentBuffer(
    DocumentUri Uri,
    int? Version,
    string Text,
    IReadOnlyCollection<IdeSupportTag>? Tags = null,
    long Generation = 0);

/// <summary>Tracks the live text and tag state of documents opened in the editor.</summary>
public interface IDocumentBufferService
{
    /// <summary>Replaces the buffer for the given document with new text and version, discarding any cached tags.</summary>
    /// <param name="generation">
    /// The document's content generation for this text, as returned by bumping it. Defaults to 0 for
    /// callers that don't track generations; the server's own sync handler always passes it.
    /// An update carrying a lower generation than the buffer holds is ignored.
    /// </param>
    void Update(DocumentUri uri, int? version, string text, long generation = 0);
    /// <summary>Updates the cached Gherkin tags of an existing buffer. Never creates a buffer: returns <see langword="false"/> when the document is not (or no longer) open.</summary>
    bool UpdateTags(DocumentUri uri, IReadOnlyCollection<IdeSupportTag> tags);
    /// <summary>Removes the buffer for the given document, typically when it is closed.</summary>
    void Remove(DocumentUri uri);
    /// <summary>Attempts to retrieve the current buffer for the given document.</summary>
    bool TryGet(DocumentUri uri, out DocumentBuffer? buffer);
    /// <summary>All buffers currently tracked by the service.</summary>
    IEnumerable<DocumentBuffer> All { get; }
}

/// <summary>Default in-memory implementation of <see cref="IDocumentBufferService"/> backed by a concurrent dictionary keyed on URI.</summary>
public class DocumentBufferService : IDocumentBufferService
{
    private readonly ConcurrentDictionary<string, DocumentBuffer> _buffers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Update(DocumentUri uri, int? version, string text, long generation = 0)
    {
        // An update that carries an older generation than the buffer holds is dropped: two changes
        // to one document can reach here out of order, and the older text must not replace the newer.
        var buffer = new DocumentBuffer(uri, version, text, Generation: generation);
        _buffers.AddOrUpdate(
            uri.ToString(),
            buffer,
            (_, existing) => existing.Generation > generation ? existing : buffer);
    }

    /// <inheritdoc/>
    public bool UpdateTags(DocumentUri uri, IReadOnlyCollection<IdeSupportTag> tags)
    {
        // Update-only: a parse can finish after didClose has removed the buffer, and
        // re-creating it here would leave a ghost "open" document. TryUpdate only swaps in the
        // tagged copy if the entry is still the one just read; retry if a concurrent
        // Update/UpdateTags replaced it, and give up once Remove has taken it away.
        var key = uri.ToString();
        while (_buffers.TryGetValue(key, out var existing))
        {
            if (_buffers.TryUpdate(key, existing with { Tags = tags }, existing))
                return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public bool TryGet(DocumentUri uri, out DocumentBuffer? buffer)
        => _buffers.TryGetValue(uri.ToString(), out buffer);

    /// <inheritdoc/>
    public void Remove(DocumentUri uri)
        => _buffers.TryRemove(uri.ToString(), out _);

    /// <inheritdoc/>
    public IEnumerable<DocumentBuffer> All => _buffers.Values;
}
