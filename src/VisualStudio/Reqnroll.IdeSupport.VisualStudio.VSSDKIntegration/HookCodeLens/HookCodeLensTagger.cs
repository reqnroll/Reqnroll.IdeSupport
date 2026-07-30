#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Supplies <see cref="ICodeLensTag"/>s for hook-match-count lens locations in a <c>.feature</c>
/// buffer (issue #372) — one per <c>Feature:</c>/<c>Scenario:</c> line reported by the server's
/// <c>textDocument/codeLens</c> (<c>HookCodeLensHandler</c>, issue #269), fetched via
/// <see cref="HookCodeLensRedirect"/>. An ordinary <see cref="ITagger{T}"/>, content-type scoped —
/// unlike VS.Extensibility's <c>ICodeLensProvider</c>, this needs no Roslyn/code-element source.
/// </summary>
internal sealed class HookCodeLensTagger : ITagger<ICodeLensTag>, IDisposable
{
    private readonly ITextBuffer _buffer;
    private readonly string _filePath;
    private readonly string _fileUri;

    // Keyed by (Line, NavLine, NavChar) — not just Line — so GetTags returns the *same* ICodeLensTag
    // instance across repaint/scroll calls between refreshes, rather than a fresh one every time —
    // the classic CodeLens host (like IntelliJ's Structure View tree, see
    // [[rider-structure-view-gotchas]]) tracks tags by identity; handing back a new instance per
    // call would make it treat every call as "new tags" and needlessly tear down/recreate data
    // points on every scroll. Line alone is not a unique key: a Scenario: line carries up to two
    // lenses at once (its own-level hooks lens and, from AddStepHooksLens, a second lens for the
    // scenario's step-level hooks) — keying by Line alone collapsed them into one Dictionary slot,
    // silently dropping whichever entry HookCodeLensHandler emitted second (issue #400 live-test
    // finding). NavLine/NavChar reliably distinguish the two: the own-level lens's click target is
    // the Scenario: tag itself, the step-hooks lens's is the scenario's first step.
    private volatile IReadOnlyDictionary<(int Line, int NavLine, int NavChar), HookCodeLensTag> _tagsByLine = EmptyTags;
    private int _refreshInFlight;
    private bool _disposed;

    private static readonly IReadOnlyDictionary<(int Line, int NavLine, int NavChar), HookCodeLensTag> EmptyTags =
        new Dictionary<(int, int, int), HookCodeLensTag>();

    public HookCodeLensTagger(ITextBuffer buffer, string filePath, string fileUri)
    {
        _buffer   = buffer;
        _filePath = filePath;
        _fileUri  = fileUri;
        HookCodeLensRedirect.RegisterTagger(this, fileUri);
        RequestRefresh();
    }

    /// <inheritdoc />
    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    /// <inheritdoc />
    public IEnumerable<ITagSpan<ICodeLensTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)
            yield break;

        var snapshot   = spans[0].Snapshot;
        var lineCount  = snapshot.LineCount;

        foreach (var kvp in _tagsByLine)
        {
            var line0 = kvp.Key.Line;
            if (line0 < 0 || line0 >= lineCount)
                continue;

            var line = snapshot.GetLineFromLineNumber(line0);
            var span = new SnapshotSpan(snapshot, line.Start, 0);
            if (!spans.Any(s => s.IntersectsWith(span) || s.Contains(span.Start)))
                continue;

            yield return new TagSpan<ICodeLensTag>(span, kvp.Value);
        }
    }

    /// <summary>Kicks off an async re-pull of lens data for this buffer's file, coalescing concurrent requests. Safe to call from any thread.</summary>
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
            var fetch = HookCodeLensRedirect.GetLensesAsync;
            if (fetch is null)
                return;

            var lenses = await fetch(_fileUri, CancellationToken.None).ConfigureAwait(false);
            if (_disposed)
                return;

            var snapshot = _buffer.CurrentSnapshot;
            var previous = _tagsByLine;
            var next     = new Dictionary<(int Line, int NavLine, int NavChar), HookCodeLensTag>(lenses.Count);

            foreach (var entry in lenses)
            {
                var key = (entry.Line, entry.NavLine, entry.NavChar);
                var elementDescription = HookElementDescription.Encode(entry);

                // Reuse the previous refresh's tag instance when this entry's data hasn't actually
                // changed, so an unrelated line's count changing doesn't churn every lens.
                if (previous.TryGetValue(key, out var existing)
                    && existing.Descriptor.ElementDescription == elementDescription)
                {
                    next[key] = existing;
                    continue;
                }

                var line = entry.Line >= 0 && entry.Line < snapshot.LineCount
                    ? snapshot.GetLineFromLineNumber(entry.Line)
                    : (ITextSnapshotLine?)null;
                var span = line is null
                    ? new Span(0, 0)
                    : new Span(line.Start, 0);

                var descriptor = new HookCodeLensDescriptor(_filePath, span, elementDescription);
                next[key] = new HookCodeLensTag(descriptor);
            }

            // Tags for lines that no longer have a lens are gone — let the host know.
            foreach (var kvp in previous)
                if (!next.ContainsKey(kvp.Key))
                    kvp.Value.RaiseDisconnected();

            _tagsByLine = next;

            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch
        {
            // Best-effort background refresh — a failed pull just leaves the previous (possibly
            // empty) lens set in place until the next successful refresh.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        HookCodeLensRedirect.UnregisterTagger(this, _fileUri);
    }
}
