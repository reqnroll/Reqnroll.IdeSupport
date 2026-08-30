using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Documents;

/// <summary>The live (possibly unsaved) text of an open <c>.cs</c> document, keyed by URI.</summary>
public record CSharpFileText(DocumentUri Uri, string Text);

/// <summary>
/// Tracks the current (possibly unsaved) text of every <c>.cs</c> file the client has open,
/// independent of <see cref="IDocumentBufferService"/> — which is deliberately Gherkin-only and
/// never populated for <c>.cs</c> documents (see <c>TextDocumentSyncHandler</c>).
/// </summary>
/// <remarks>
/// Without this, any code that needs a <c>.cs</c> file's live content (e.g. Step Rename
/// refactoring locating the attribute literal to edit, or the Roslyn/C# source-level binding
/// discovery's incremental Roslyn reconciliation) had no source but disk, which is stale for
/// any unsaved edit — whether that edit came from the user typing directly in the file or from
/// this server's own Step Rename refactoring applying its own edit.
/// <c>TextDocumentSyncHandler</c> populates this on every <c>.cs</c> <c>didOpen</c>/
/// <c>didChange</c> regardless of source, and removes it on <c>didClose</c> — once closed, disk
/// is the source of truth again (VS prompts to save or discard before a close reaches the
/// server).
/// </remarks>
public interface ICSharpFileTextCache
{
    /// <summary>Records or replaces the live text for the given <c>.cs</c> document.</summary>
    void Update(DocumentUri uri, string text);
    /// <summary>Attempts to retrieve the live text for the given <c>.cs</c> document.</summary>
    bool TryGet(DocumentUri uri, out string? text);
    /// <summary>Removes the cached text for the given <c>.cs</c> document, typically on <c>didClose</c>.</summary>
    void Remove(DocumentUri uri);
    /// <summary>All <c>.cs</c> documents currently tracked by the cache.</summary>
    IEnumerable<CSharpFileText> All { get; }
}

/// <summary>Default in-memory implementation of <see cref="ICSharpFileTextCache"/> backed by a concurrent dictionary keyed on URI.</summary>
public sealed class CSharpFileTextCache : ICSharpFileTextCache
{
    private readonly ConcurrentDictionary<string, CSharpFileText> _texts = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Update(DocumentUri uri, string text)
        => _texts[uri.ToString()] = new CSharpFileText(uri, text);

    /// <inheritdoc/>
    public bool TryGet(DocumentUri uri, out string? text)
    {
        if (_texts.TryGetValue(uri.ToString(), out var entry))
        {
            text = entry.Text;
            return true;
        }

        text = null;
        return false;
    }

    /// <inheritdoc/>
    public void Remove(DocumentUri uri)
        => _texts.TryRemove(uri.ToString(), out _);

    /// <inheritdoc/>
    public IEnumerable<CSharpFileText> All => _texts.Values;
}
