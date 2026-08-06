#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Generic classic-CodeLens <see cref="ITagger{T}"/>: supplies one <see cref="ICodeLensTag"/> per
/// line, built from a live-fetched, line-grouped entry list — the tagger-side half of every
/// Reqnroll classic CodeLens feature (issue #372/#262). Extracted from what were two near-identical
/// copies (<c>HookCodeLensTagger</c>, <c>RunTestCodeLensTagger</c>); each feature now only supplies
/// the fetch/group/encode functions, not the tag-lifecycle machinery itself.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one tag per line, even when a feature reports more than one entry kind for it (e.g. a
/// Scenario: line carrying both an own-level hooks lens and a step-hooks lens) — the classic
/// CodeLens engine renders one adornment row per location and lets every registered
/// <c>IAsyncCodeLensDataPointProvider</c> contribute an indicator to it (the same shape Roslyn uses
/// for "N references | N changes" on a C# member). A feature that needs several entry kinds on one
/// line groups them under this same tag and lets its own data-point providers pick out their kind —
/// see <c>HookLensSupport</c>'s original remarks (now folded into that feature's own code) for why
/// splitting them into separate tags never worked.
/// </para>
/// <para>
/// Keyed by 0-based line so <see cref="GetTags"/> returns the *same* <see cref="ICodeLensTag"/>
/// instance across repaint/scroll calls between refreshes, rather than a fresh one every time — the
/// classic CodeLens host (like IntelliJ's Structure View tree, see
/// [[rider-structure-view-gotchas]]) tracks tags by identity; handing back a new instance per call
/// would make it treat every call as "new tags" and needlessly tear down/recreate data points on
/// every scroll.
/// </para>
/// </remarks>
internal sealed class LineKeyedCodeLensTagger<TEntry> : ITagger<ICodeLensTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly string _filePath;
    private readonly string _fileUri;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<TEntry>?>> _fetch;
    private readonly Func<TEntry, int> _lineSelector;
    private readonly Func<int, IEnumerable<TEntry>, string> _elementDescriptionEncoder;
    private readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<TEntry>> _registry;

    private volatile IReadOnlyDictionary<int, LineCodeLensTag> _tagsByLine = EmptyTags;
    private int _refreshInFlight;
    private bool _disposed;

    private static readonly IReadOnlyDictionary<int, LineCodeLensTag> EmptyTags = new Dictionary<int, LineCodeLensTag>();

    /// <param name="fetch">Fetches every entry for the whole file. Returns <see langword="null"/> (as opposed to an empty list) when the data source isn't wired up yet (e.g. the LSP connection isn't live) — a refresh triggered while <see langword="null"/> leaves the previous (possibly empty) tag set in place rather than clearing it.</param>
    /// <param name="lineSelector">The 0-based line an entry belongs on.</param>
    /// <param name="elementDescriptionEncoder">Builds the opaque, content-derived <c>ElementDescription</c> for one line's group of entries — see <see cref="LineElementDescription"/>.</param>
    /// <param name="registry">The shared registry this tagger registers itself with for cross-instance invalidation.</param>
    public LineKeyedCodeLensTagger(
        ITextBuffer buffer, string filePath, string fileUri,
        Func<string, CancellationToken, Task<IReadOnlyList<TEntry>?>> fetch,
        Func<TEntry, int> lineSelector,
        Func<int, IEnumerable<TEntry>, string> elementDescriptionEncoder,
        WeakTaggerRegistry<LineKeyedCodeLensTagger<TEntry>> registry)
    {
        _buffer = buffer;
        _filePath = filePath;
        _fileUri = fileUri;
        _fetch = fetch;
        _lineSelector = lineSelector;
        _elementDescriptionEncoder = elementDescriptionEncoder;
        _registry = registry;
        _registry.RegisterTagger(this, fileUri);
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

    /// <summary>Kicks off an async re-pull of entry data for this buffer's file, coalescing concurrent requests. Safe to call from any thread.</summary>
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
            var entries = await _fetch(_fileUri, CancellationToken.None).ConfigureAwait(false);
            if (entries is null || _disposed)
                return;

            var snapshot = _buffer.CurrentSnapshot;
            var previous = _tagsByLine;
            var next = new Dictionary<int, LineCodeLensTag>();

            foreach (var group in entries.GroupBy(_lineSelector))
            {
                var elementDescription = _elementDescriptionEncoder(group.Key, group);

                // Reuse the previous refresh's tag instance when this line's data hasn't actually
                // changed, so an unrelated line's content changing doesn't churn every lens.
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

                var descriptor = new LineCodeLensDescriptor(_filePath, span, elementDescription);
                next[group.Key] = new LineCodeLensTag(descriptor);
            }

            // Tags for lines that no longer have an entry are gone — let the host know.
            foreach (var kvp in previous)
                if (!next.ContainsKey(kvp.Key))
                    kvp.Value.RaiseDisconnected();

            _tagsByLine = next;

            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch
        {
            // Best-effort background refresh — a failed pull just leaves the previous (possibly
            // empty) entry set in place until the next successful refresh.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _registry.UnregisterTagger(this, _fileUri);
    }
}
