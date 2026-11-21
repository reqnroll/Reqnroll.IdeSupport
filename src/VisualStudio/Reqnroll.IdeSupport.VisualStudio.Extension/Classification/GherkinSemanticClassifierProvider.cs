using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

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
[ContentType(VsWellKnownIds.GherkinContentType)]
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
/// classification spans against the <c>IdeSupportClassifications</c> classification type whose name
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

        // The store is a process-wide singleton, and nothing tells a classifier when its buffer closes,
        // so subscribing directly would root this classifier (and its buffer) for the whole VS session.
        WeakTokensChangedSubscription.Subscribe(_store, this);

        // Until the server's next push arrives, the cached tokens describe the text before the edit. Moving them
        // through each edit, before the editor asks for the new classifications, keeps the colours of the lines
        // below an inserted or deleted line (and of the rest of an edited line) where their text now is.
        WeakBufferChangedSubscription.Subscribe(_buffer, this);
    }

    private void OnBufferChanged(TextContentChangedEventArgs e)
    {
        if (_fileKey is null || e.Changes.Count == 0)
            return;

        // The changes of one event are ordered by position; later ones are applied first so the coordinates of the
        // earlier ones (taken from the text before the edit) are not disturbed.
        var changes = new List<TokenTextChange>(e.Changes.Count);
        for (var i = e.Changes.Count - 1; i >= 0; i--)
            changes.Add(ToTokenTextChange(e.Changes[i], e.Before, e.After));

        _store.ApplyTextChanges(_fileKey, changes);
    }

    /// <summary>Describes one edit of a buffer in line and character coordinates, as the token store needs it.</summary>
    internal static TokenTextChange ToTokenTextChange(ITextChange change, ITextSnapshot before, ITextSnapshot after)
    {
        var oldStart = before.GetLineFromPosition(change.OldPosition);
        var oldEnd = before.GetLineFromPosition(change.OldEnd);
        var newEnd = after.GetLineFromPosition(change.NewEnd);
        return new TokenTextChange(
            oldStart.LineNumber, change.OldPosition - oldStart.Start.Position,
            oldEnd.LineNumber, change.OldEnd - oldEnd.Start.Position,
            newEnd.LineNumber, change.NewEnd - newEnd.Start.Position);
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

    private void OnTokensChanged(string fileKey, int startLine, int endLine)
    {
        if (!string.Equals(fileKey, _fileKey, StringComparison.OrdinalIgnoreCase))
            return;

        var snapshot = _buffer.CurrentSnapshot;
        ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(ChangedSpan(snapshot, startLine, endLine)));
    }

    /// <summary>
    /// The span to re-classify when the tokens on <paramref name="startLine"/>..<paramref name="endLine"/> changed:
    /// just those lines (clamped to the snapshot), or the whole snapshot when no usable line range is given.
    /// </summary>
    internal static SnapshotSpan ChangedSpan(ITextSnapshot snapshot, int startLine, int endLine)
    {
        if (startLine < 0 || endLine < startLine || snapshot.LineCount == 0)
            return new SnapshotSpan(snapshot, 0, snapshot.Length);

        var first = snapshot.GetLineFromLineNumber(Math.Min(startLine, snapshot.LineCount - 1));
        var last = snapshot.GetLineFromLineNumber(Math.Min(endLine, snapshot.LineCount - 1));
        return new SnapshotSpan(snapshot, first.Start.Position, last.End.Position - first.Start.Position);
    }

    /// <summary>
    /// Forwards the buffer's high-priority change event to a classifier held only weakly, so a buffer that is kept
    /// alive by something else does not also keep its classifier (and the closures it holds) alive.
    /// </summary>
    private sealed class WeakBufferChangedSubscription
    {
        private readonly ITextBuffer _buffer;
        private readonly WeakReference<GherkinSemanticClassifier> _classifier;

        private WeakBufferChangedSubscription(ITextBuffer buffer, GherkinSemanticClassifier classifier)
        {
            _buffer = buffer;
            _classifier = new WeakReference<GherkinSemanticClassifier>(classifier);
        }

        public static void Subscribe(ITextBuffer buffer, GherkinSemanticClassifier classifier) =>
            buffer.ChangedHighPriority += new WeakBufferChangedSubscription(buffer, classifier).OnChanged;

        private void OnChanged(object? sender, TextContentChangedEventArgs e)
        {
            if (_classifier.TryGetTarget(out var classifier))
                classifier.OnBufferChanged(e);
            else
                _buffer.ChangedHighPriority -= OnChanged;
        }
    }

    /// <summary>
    /// Forwards <see cref="SemanticTokenClassificationStore.TokensChanged"/> to a classifier held only weakly,
    /// so the store keeps nothing alive but this small forwarder. Once the classifier has been collected, the
    /// forwarder unsubscribes itself the next time the event is raised.
    /// </summary>
    private sealed class WeakTokensChangedSubscription
    {
        private readonly SemanticTokenClassificationStore _store;
        private readonly WeakReference<GherkinSemanticClassifier> _classifier;

        private WeakTokensChangedSubscription(SemanticTokenClassificationStore store, GherkinSemanticClassifier classifier)
        {
            _store = store;
            _classifier = new WeakReference<GherkinSemanticClassifier>(classifier);
        }

        public static void Subscribe(SemanticTokenClassificationStore store, GherkinSemanticClassifier classifier) =>
            store.TokensChanged += new WeakTokensChangedSubscription(store, classifier).OnTokensChanged;

        private void OnTokensChanged(string fileKey, int startLine, int endLine)
        {
            if (_classifier.TryGetTarget(out var classifier))
                classifier.OnTokensChanged(fileKey, startLine, endLine);
            else
                _store.TokensChanged -= OnTokensChanged;
        }
    }
}