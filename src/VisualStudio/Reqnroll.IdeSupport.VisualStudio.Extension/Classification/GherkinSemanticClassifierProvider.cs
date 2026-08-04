using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

/// <summary>
/// Provides the <see cref="GherkinSemanticClassifier"/> for <c>.feature</c> buffers, colouring them
/// from the LSP server's semantic tokens via Reqnroll's custom classifications.
/// </summary>
/// <remarks>
/// This bypasses Visual Studio's built-in LSP semantic-token colorizer, whose token-type→classification
/// mapping is a fixed internal table that cannot resolve Reqnroll's custom <c>reqnroll.*</c> token types.
/// </remarks>
[Export(typeof(IClassifierProvider))]
[ContentType("Gherkin")]
internal sealed class GherkinSemanticClassifierProvider : IClassifierProvider
{
    [Import] internal IClassificationTypeRegistryService ClassificationRegistry { get; set; } = null!;

    [Import] internal ITextDocumentFactoryService TextDocumentFactory { get; set; } = null!;

    /// <summary>Gets or creates a <see cref="GherkinSemanticClassifier"/> for the given text buffer.</summary>
    public IClassifier GetClassifier(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() =>
            new GherkinSemanticClassifier(
                textBuffer, ClassificationRegistry, TextDocumentFactory, SemanticTokenClassificationStore.Instance));
}

/// <summary>
/// Reads the cached semantic tokens for the buffer's <c>.feature</c> file and produces
/// classification spans against the <c>DeveroomClassifications</c> classification type whose name
/// matches each token's legend type name (e.g. <c>reqnroll.keyword</c>).
/// </summary>
internal sealed class GherkinSemanticClassifier : IClassifier
{
    private readonly ITextBuffer _buffer;
    private readonly IClassificationTypeRegistryService _registry;
    private readonly SemanticTokenClassificationStore _store;
    private readonly string? _fileKey;

    // Cache classification-type lookups by legend name (value may be null when unregistered).
    private readonly ConcurrentDictionary<string, IClassificationType?> _typeCache =
        new ConcurrentDictionary<string, IClassificationType?>(StringComparer.Ordinal);

    /// <summary>Raised when the classification of the buffer's text changes.</summary>
    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;

    /// <summary>Initialises the classifier with the buffer, classification registry, document factory, and token store.</summary>
    public GherkinSemanticClassifier(
        ITextBuffer buffer,
        IClassificationTypeRegistryService registry,
        ITextDocumentFactoryService textDocumentFactory,
        SemanticTokenClassificationStore store)
    {
        _buffer = buffer;
        _registry = registry;
        _store = store;

        _fileKey = textDocumentFactory.TryGetTextDocument(buffer, out var document)
            ? SemanticTokenClassificationStore.NormalizeKey(document.FilePath)
            : null;

        _store.TokensChanged += OnTokensChanged;
    }

    /// <summary>Returns classification spans for the given snapshot span by looking up the cached semantic tokens.</summary>
    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        var result = new List<ClassificationSpan>();
        if (_fileKey is null || !_store.TryGetTokens(_fileKey, out var tokens) || tokens.Count == 0)
            return result;

        var snapshot = span.Snapshot;

        foreach (var token in tokens)
        {
            if (token.Line < 0 || token.Line >= snapshot.LineCount)
                continue;

            var line = snapshot.GetLineFromLineNumber(token.Line);
            int start = line.Start.Position + token.StartChar;
            int end = start + token.Length;

            // Clamp to the line / snapshot in case the buffer changed since these tokens were produced.
            if (start < 0 || start > line.End.Position)
                continue;
            if (end > line.End.Position)
                end = line.End.Position;
            if (end <= start)
                continue;

            var tokenSpan = new SnapshotSpan(snapshot, start, end - start);
            if (!tokenSpan.IntersectsWith(span))
                continue;

            var classificationType = ResolveType(token.TokenType);
            if (classificationType is not null)
                result.Add(new ClassificationSpan(tokenSpan, classificationType));
        }

        return result;
    }

    private IClassificationType? ResolveType(string tokenType) =>
        _typeCache.GetOrAdd(tokenType, name => _registry.GetClassificationType(name));

    private void OnTokensChanged(string fileKey)
    {
        if (!string.Equals(fileKey, _fileKey, StringComparison.OrdinalIgnoreCase))
            return;

        var snapshot = _buffer.CurrentSnapshot;
        ClassificationChanged?.Invoke(
            this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }
}