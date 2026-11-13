#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Generic classic-CodeLens <see cref="ITagger{T}"/>: supplies one <see cref="ICodeLensTag"/> per
/// line, built from a live-fetched, line-grouped entry list — the tagger-side half of every
/// Reqnroll classic CodeLens feature. Each feature only supplies
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
/// <para>
/// Deliberately not <see cref="IDisposable"/>. The tagger providers hand every consumer
/// of a buffer the same instance (a singleton in <see cref="ITextBuffer.Properties"/>), and VS's tag
/// aggregator disposes each <see cref="IDisposable"/> tagger it got from a provider when that
/// aggregator goes away (its view closes) or the buffer's content type changes, regardless of who
/// else still uses it — so one view closing would have frozen the lenses in every other view on the
/// same buffer. The instance lives exactly as long as its buffer's property bag, and
/// <see cref="WeakTaggerRegistry{TTagger}"/> only holds it weakly, so it needs no explicit teardown.
/// </para>
/// </remarks>
internal sealed class LineKeyedCodeLensTagger<TEntry> : ITagger<ICodeLensTag>
{
    private readonly ITextBuffer _buffer;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<TEntry>?>> _fetch;
    private readonly Func<TEntry, int> _lineSelector;
    private readonly Func<int, IEnumerable<TEntry>, string> _elementDescriptionEncoder;
    private readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<TEntry>> _registry;

    // Guards the swap of _location and _tagsByLine together, so a refresh that fetched for the old
    // location can't publish its tags after a rename has replaced that location. Both fields are
    // still read without it (volatile) on the GetTags / fetch paths.
    private readonly object _gate = new();
    private volatile FileLocation _location;
    private volatile IReadOnlyDictionary<int, LineCodeLensTag> _tagsByLine = EmptyTags;
    private int _refreshInFlight;
    private int _refreshPending;

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
        _location = new FileLocation(filePath, fileUri);
        _fetch = fetch;
        _lineSelector = lineSelector;
        _elementDescriptionEncoder = elementDescriptionEncoder;
        _registry = registry;
        registry.RegisterTagger(this, fileUri);
        RequestRefresh();
    }

    /// <summary>
    /// Returns the buffer's shared tagger, creating it on first use. Returns <see langword="null"/>
    /// when the buffer has no <see cref="ITextDocument"/> or its path can't be turned into a URI.
    /// The new tagger follows the document's renames (including Save As) so it keeps fetching for,
    /// and registering under, the file's current URI rather than the one it was created with.
    /// </summary>
    internal static LineKeyedCodeLensTagger<TEntry>? GetOrCreateForBuffer(
        ITextBuffer buffer,
        Func<string, CancellationToken, Task<IReadOnlyList<TEntry>?>> fetch,
        Func<TEntry, int> lineSelector,
        Func<int, IEnumerable<TEntry>, string> elementDescriptionEncoder,
        WeakTaggerRegistry<LineKeyedCodeLensTagger<TEntry>> registry)
    {
        if (buffer.Properties.TryGetProperty(typeof(LineKeyedCodeLensTagger<TEntry>), out LineKeyedCodeLensTagger<TEntry> existing))
            return existing;

        if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return null;

        var filePath = doc.FilePath;
        var fileUri = TryCreateFileUri(filePath);
        if (fileUri is null)
            return null;

        return buffer.Properties.GetOrCreateSingletonProperty(
            typeof(LineKeyedCodeLensTagger<TEntry>),
            () =>
            {
                var tagger = new LineKeyedCodeLensTagger<TEntry>(
                    buffer, filePath, fileUri, fetch, lineSelector, elementDescriptionEncoder, registry);
                // The document lives exactly as long as the buffer that owns this tagger, so the
                // subscription keeps nothing alive that wasn't already.
                doc.FileActionOccurred += tagger.OnFileActionOccurred;
                return tagger;
            });
    }

    /// <summary>The URI this tagger currently fetches for and is registered under.</summary>
    internal string FileUri => _location.FileUri;

    private static string? TryCreateFileUri(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;
        try
        {
            return new Uri(filePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private void OnFileActionOccurred(object? sender, TextDocumentFileActionEventArgs e)
    {
        if ((e.FileActionType & FileActionTypes.DocumentRenamed) != 0)
            OnDocumentRenamed(e.FilePath);
    }

    /// <summary>
    /// Re-keys the tagger to <paramref name="newFilePath"/>: moves its registry entry to the new
    /// URI, drops every tag (their descriptors carry the old path), and re-fetches for the new URI.
    /// A path that can't be turned into a URI leaves the tagger as it was.
    /// </summary>
    internal void OnDocumentRenamed(string? newFilePath)
    {
        if (newFilePath is null || TryCreateFileUri(newFilePath) is not { } newUri)
        {
            LogVerbose($"document renamed to '{newFilePath}', which is not a valid URI; keeping {_location.FileUri}");
            return;
        }

        FileLocation old;
        IReadOnlyDictionary<int, LineCodeLensTag> dropped;
        lock (_gate)
        {
            old = _location;
            if (string.Equals(old.FilePath, newFilePath, StringComparison.Ordinal))
                return;
            _location = new FileLocation(newFilePath, newUri);
            dropped = _tagsByLine;
            _tagsByLine = EmptyTags;
        }

        _registry.UnregisterTagger(this, old.FileUri);
        _registry.RegisterTagger(this, newUri);

        LogVerbose($"document renamed; re-keyed from {old.FileUri} to {newUri}, dropped {dropped.Count} tag(s)");

        foreach (var tag in dropped.Values)
            tag.RaiseDisconnected();

        var snapshot = _buffer.CurrentSnapshot;
        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));

        RequestRefresh();
    }

    private void LogVerbose(string message) =>
        (Logger ?? ExtensionHostLogger.Instance).Log(new LogMessage(TraceLevel.Verbose,
            $"LineKeyedCodeLensTagger<{typeof(TEntry).Name}>: {message}",
            nameof(OnDocumentRenamed), null, nameof(LineKeyedCodeLensTagger<TEntry>)));

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

    /// <summary>
    /// Test seam: invoked by <see cref="RequestRefresh"/> on the calling thread right after
    /// it finds another refresh already in flight, before it returns. Lets a test complete that in-flight
    /// refresh at exactly this point to drive the lost-wakeup interleaving deterministically. Always
    /// <see langword="null"/> in production.
    /// </summary>
    internal Action? RefreshInFlightObserved { get; set; }

    /// <summary>
    /// Where a failed refresh is reported. <see langword="null"/> (the production default) means
    /// <see cref="ExtensionHostLogger.Instance"/>; settable so tests can observe the report.
    /// </summary>
    internal IIdeSupportLogger? Logger { get; set; }

    /// <summary>
    /// Kicks off an async re-pull of entry data for this buffer's file, coalescing concurrent
    /// requests. Safe to call from any thread. A request that arrives while one is already in flight
    /// is not dropped — it is recorded and re-run once the in-flight one finishes (fresh-eyes review
    /// finding: the previous version discarded it outright, so a request landing between two other
    /// refreshes could be lost until some unrelated later refresh happened to occur).
    /// </summary>
    /// <remarks>
    /// The request is recorded in <c>_refreshPending</c> <em>before</em> trying to claim
    /// <c>_refreshInFlight</c>. Recording it only after a failed claim would leave a window in
    /// which the in-flight refresh could clear <c>_refreshInFlight</c>, find nothing pending and stop,
    /// and only then would the pending flag be set, with no one left to act on it. A failed claim
    /// means the owner had not yet cleared <c>_refreshInFlight</c>, so its later read of
    /// <c>_refreshPending</c> (all accesses are interlocked, so fully ordered) is bound to see this
    /// request. The caller that does claim the slot clears the flag before fetching: its fetch starts
    /// after every request recorded so far, so it covers them, and leaving the flag set would make
    /// every refresh immediately schedule another.
    /// </remarks>
    internal void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshPending, 1);

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            RefreshInFlightObserved?.Invoke();
            return; // the in-flight refresh's finally block will see _refreshPending and run again
        }

        Interlocked.Exchange(ref _refreshPending, 0);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var location = _location;
        try
        {
            var entries = await _fetch(location.FileUri, CancellationToken.None).ConfigureAwait(false);
            if (entries is null)
                return;

            var snapshot = _buffer.CurrentSnapshot;
            var stale = new List<LineCodeLensTag>();
            lock (_gate)
            {
                // The document was renamed while this fetch ran: these entries belong to the old URI.
                // The rename queued its own refresh, which the finally block below runs.
                if (!ReferenceEquals(location, _location))
                    return;

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

                    var descriptor = new LineCodeLensDescriptor(location.FilePath, span, elementDescription);
                    next[group.Key] = new LineCodeLensTag(descriptor);
                }

                // Tags that are gone — the line has no entry any more, or its description changed and a
                // new tag instance replaced it — get Disconnected so the host drops their data points
                // instead of keeping a stale lens alongside the new one.
                foreach (var kvp in previous)
                    if (!next.TryGetValue(kvp.Key, out var replacement) || !ReferenceEquals(replacement, kvp.Value))
                        stale.Add(kvp.Value);

                _tagsByLine = next;
            }

            foreach (var tag in stale)
                tag.RaiseDisconnected();

            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch (Exception ex)
        {
            // Best-effort background refresh — a failed pull just leaves the previous (possibly
            // empty) entry set in place until the next successful refresh. Info rather than Warning:
            // a Warning would bring the Output window to the front for a background lens refresh.
            (Logger ?? ExtensionHostLogger.Instance).Log(new LogMessage(TraceLevel.Info,
                $"CodeLens refresh for {location.FileUri} failed; keeping the previous lenses until the next refresh",
                nameof(RefreshAsync), ex, nameof(LineKeyedCodeLensTagger<TEntry>)));
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
            // A RequestRefresh arrived while the above was in flight — its caller was told "you're
            // covered", but the fetch above may have started before that request's reason for asking
            // even existed, so honour it with one more refresh instead of treating this one as good enough.
            if (Interlocked.Exchange(ref _refreshPending, 0) == 1)
                RequestRefresh();
        }
    }

    private sealed class FileLocation
    {
        public FileLocation(string filePath, string fileUri)
        {
            FilePath = filePath;
            FileUri = fileUri;
        }

        public string FilePath { get; }
        public string FileUri { get; }
    }
}
