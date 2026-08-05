#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Supplies <see cref="ICodeLensTag"/>s for Run-lens locations in a <c>.feature</c> buffer (design
/// doc §5/§6, issue #262) — one per Scenario/Scenario Outline line that has at least one resolved
/// test target, fetched via <see cref="RunTestCodeLensRedirect"/>. Mirrors <c>HookCodeLensTagger</c>
/// exactly, including the tag-instance-reuse behavior it documents at length.
/// </summary>
internal sealed class RunTestCodeLensTagger : ITagger<ICodeLensTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly string _filePath;
    private readonly string _fileUri;

    private volatile IReadOnlyDictionary<int, RunTestCodeLensTag> _tagsByLine = EmptyTags;
    private int _refreshInFlight;
    private bool _disposed;

    private static readonly IReadOnlyDictionary<int, RunTestCodeLensTag> EmptyTags =
        new Dictionary<int, RunTestCodeLensTag>();

    public RunTestCodeLensTagger(ITextBuffer buffer, string filePath, string fileUri)
    {
        _buffer = buffer;
        _filePath = filePath;
        _fileUri = fileUri;
        RunTestCodeLensRedirect.RegisterTagger(this, fileUri);
        RequestRefresh();
    }

    /// <inheritdoc />
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    /// <inheritdoc />
    public IEnumerable<ITagSpan<ICodeLensTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)
            yield break;

        var snapshot = spans[0].Snapshot;
        var lineCount = snapshot.LineCount;

        foreach (var kvp in _tagsByLine)
        {
            var line0 = kvp.Key;
            if (line0 < 0 || line0 >= lineCount)
                continue;

            var line = snapshot.GetLineFromLineNumber(line0);
            var span = new SnapshotSpan(snapshot, line.Start, 0);
            if (!spans.Any(s => s.IntersectsWith(span) || s.Contains(span.Start)))
                continue;

            yield return new TagSpan<ICodeLensTag>(span, kvp.Value);
        }
    }

    /// <summary>Kicks off an async re-pull of target data for this buffer's file, coalescing concurrent requests. Safe to call from any thread.</summary>
    internal void RequestRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var fetch = RunTestCodeLensRedirect.GetTargetsAsync;
            if (fetch is null)
                return;

            var entries = await fetch(_fileUri, CancellationToken.None).ConfigureAwait(false);
            if (_disposed)
                return;

            var snapshot = _buffer.CurrentSnapshot;
            var previous = _tagsByLine;
            var next = new Dictionary<int, RunTestCodeLensTag>();

            foreach (var group in entries.GroupBy(e => e.Line))
            {
                var elementDescription = RunElementDescription.Encode(group.Key, group);

                if (previous.TryGetValue(group.Key, out var existing)
                    && existing.Descriptor.ElementDescription == elementDescription)
                {
                    next[group.Key] = existing;
                    continue;
                }

                var line = group.Key >= 0 && group.Key < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(group.Key)
                    : (ITextSnapshotLine?)null;
                var span = line is null
                    ? new Span(0, 0)
                    : new Span(line.Start, 0);

                var descriptor = new RunTestCodeLensDescriptor(_filePath, span, elementDescription);
                next[group.Key] = new RunTestCodeLensTag(descriptor);
            }

            foreach (var kvp in previous)
                if (!next.ContainsKey(kvp.Key))
                    kvp.Value.RaiseDisconnected();

            _tagsByLine = next;

            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch
        {
            // Best-effort background refresh — a failed pull just leaves the previous (possibly
            // empty) target set in place until the next successful refresh.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        RunTestCodeLensRedirect.UnregisterTagger(this, _fileUri);
    }
}
