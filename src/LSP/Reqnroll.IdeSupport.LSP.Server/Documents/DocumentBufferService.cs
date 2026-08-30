using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Documents;

/// <summary>The in-memory snapshot of an open document's URI, version, text, and any cached Gherkin tags.</summary>
public record DocumentBuffer(DocumentUri Uri, int? Version, string Text, IReadOnlyCollection<DeveroomTag>? Tags = null);

/// <summary>Tracks the live text and tag state of documents opened in the editor.</summary>
public interface IDocumentBufferService
{
    /// <summary>Replaces the buffer for the given document with new text and version, discarding any cached tags.</summary>
    void Update(DocumentUri uri, int? version, string text);
    /// <summary>Updates the cached Gherkin tags for a document, creating an empty buffer entry if none exists yet.</summary>
    void UpdateTags(DocumentUri uri, IReadOnlyCollection<DeveroomTag> tags);
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
    public void Update(DocumentUri uri, int? version, string text)
        => _buffers[uri.ToString()] = new DocumentBuffer(uri, version, text);

    /// <inheritdoc/>
    public void UpdateTags(DocumentUri uri, IReadOnlyCollection<DeveroomTag> tags)
        => _buffers.AddOrUpdate(
            uri.ToString(),
            _ => new DocumentBuffer(uri, null, string.Empty, tags),
            (_, existing) => existing with { Tags = tags });

    /// <inheritdoc/>
    public bool TryGet(DocumentUri uri, out DocumentBuffer? buffer)
        => _buffers.TryGetValue(uri.ToString(), out buffer);

    /// <inheritdoc/>
    public void Remove(DocumentUri uri)
        => _buffers.TryRemove(uri.ToString(), out _);

    /// <inheritdoc/>
    public IEnumerable<DocumentBuffer> All => _buffers.Values;
}
