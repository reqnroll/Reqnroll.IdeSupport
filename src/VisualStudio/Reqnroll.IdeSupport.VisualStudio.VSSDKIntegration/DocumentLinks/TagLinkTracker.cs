#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

/// <summary>One clickable tag as the editor sees it: a span that follows edits, and the URL it opens.</summary>
internal readonly record struct TrackedLink(ITrackingSpan Span, string Target);

/// <summary>
/// Per-buffer cache of the clickable-tag links of one <c>.feature</c> file (issue #921), shared by
/// <see cref="TagLinkClassifier"/> (permanent link styling) and <see cref="TagLinkQuickInfoSourceProvider"/>
/// (hover target). The editor asks for classifications and quick info synchronously and constantly, so the links are
/// fetched from the server in the background through <see cref="TagLinkRedirect.GetLinksAsync"/> and the last answer is
/// served from here.
/// </summary>
/// <remarks>
/// <para>
/// Refreshed when the buffer changes (debounced), and when the server says its bindings or configuration changed
/// (<see cref="TagLinkRedirect.InvalidateAll"/>, from the CodeLens refresh the project's tag patterns arrive with).
/// Between refreshes each link is a tracking span, so the styling follows edits instead of lagging behind them.
/// </para>
/// <para>
/// Only http(s) targets are kept - see <see cref="TagLinkRedirect.IsOpenableUrl"/> - so nothing the server hands over
/// is ever styled as a link unless a click on it would be allowed to open it.
/// </para>
/// </remarks>
internal sealed class TagLinkTracker
{
    /// <summary>How long after the last edit the links are re-requested.</summary>
    internal const int DebounceMilliseconds = 400;

    // Trackers are invalidated wholesale, never per file, so they share one registry bucket.
    internal const string RegistryKey = "*";

    private readonly ITextBuffer _buffer;
    private readonly IIdeSupportLogger? _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _refresh;
    private volatile IReadOnlyList<TrackedLink> _links = Array.Empty<TrackedLink>();

    /// <summary>Raised, from any thread, when the set of links changed; the span covers the whole buffer.</summary>
    public event EventHandler<SnapshotSpanEventArgs>? LinksChanged;

    internal TagLinkTracker(ITextBuffer buffer, IIdeSupportLogger? logger)
    {
        _buffer = buffer;
        _logger = logger;
        buffer.PostChanged += OnBufferChanged;
        // Registered up front, before the server link source or the file path may exist: a restored tab's buffer is
        // classified before the language client connects, and the connection's InvalidateAll must reach it.
        TagLinkRedirect.TrackerRegistry.RegisterTagger(this, RegistryKey);
        ScheduleRefresh(0);
    }

    /// <summary>The tracker of <paramref name="buffer"/>, created on first use.</summary>
    public static TagLinkTracker GetOrCreate(ITextBuffer buffer, IIdeSupportLogger? logger) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new TagLinkTracker(buffer, logger));

    /// <summary>The links known at the moment.</summary>
    public IReadOnlyList<TrackedLink> Links => _links;

    /// <summary>Re-requests the links from the server now. Safe to call from any thread.</summary>
    public void RequestRefresh() => ScheduleRefresh(0);

    /// <summary>The link covering <paramref name="point"/>, if any.</summary>
    public TrackedLink? FindLinkAt(SnapshotPoint point)
    {
        foreach (var link in _links)
        {
            if (link.Span.GetSpan(point.Snapshot).Contains(point))
                return link;
        }
        return null;
    }

    /// <summary>
    /// The span a server-reported link covers in <paramref name="snapshot"/>, or <see langword="null"/> when its
    /// position does not exist there (an answer computed for an older text than the snapshot it arrives at).
    /// </summary>
    internal static SnapshotSpan? TryGetSpan(ITextSnapshot snapshot, TagLinkEntry link)
    {
        if (link.StartLine < 0 || link.StartLine > link.EndLine || link.EndLine >= snapshot.LineCount)
            return null;

        var start = snapshot.GetLineFromLineNumber(link.StartLine).Start.Position + link.StartChar;
        var end = snapshot.GetLineFromLineNumber(link.EndLine).Start.Position + link.EndChar;
        if (link.StartChar < 0 || link.EndChar < 0 || end <= start || end > snapshot.Length)
            return null;

        return new SnapshotSpan(snapshot, start, end - start);
    }

    private void OnBufferChanged(object? sender, EventArgs e) => ScheduleRefresh(DebounceMilliseconds);

    private void ScheduleRefresh(int delayMilliseconds)
    {
        CancellationTokenSource refresh;
        lock (_gate)
        {
            _refresh?.Cancel();
            refresh = _refresh = new CancellationTokenSource();
        }

        _ = RefreshAsync(delayMilliseconds, refresh.Token);
    }

    private async Task RefreshAsync(int delayMilliseconds, CancellationToken ct)
    {
        try
        {
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds, ct).ConfigureAwait(false);

            var getLinks = TagLinkRedirect.GetLinksAsync;
            var fileUri = TryGetFileUri();
            if (getLinks is null || fileUri is null)
                return;

            var entries = await getLinks(fileUri, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var snapshot = _buffer.CurrentSnapshot;
            var links = new List<TrackedLink>();
            foreach (var entry in entries)
            {
                if (!TagLinkRedirect.IsOpenableUrl(entry.Target))
                    continue;
                var span = TryGetSpan(snapshot, entry);
                if (span is not null)
                    links.Add(new TrackedLink(snapshot.CreateTrackingSpan(span.Value, SpanTrackingMode.EdgeExclusive), entry.Target));
            }

            if (SameLinks(snapshot, _links, links))
                return;

            _links = links;
            LinksChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh, or the server went away: the next refresh sorts it out.
        }
        catch (Exception ex)
        {
            // Server failures degrade to "keep what we have", never an error popup.
            _logger?.LogWarning($"TagLinkTracker: tag link refresh failed: {ex.Message}");
        }
    }

    // Refreshes arrive on every keystroke pause; repainting the whole buffer for an identical answer is wasted work.
    private static bool SameLinks(ITextSnapshot snapshot, IReadOnlyList<TrackedLink> current, IReadOnlyList<TrackedLink> next)
    {
        if (current.Count != next.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Target != next[i].Target ||
                current[i].Span.GetSpan(snapshot) != next[i].Span.GetSpan(snapshot))
                return false;
        }
        return true;
    }

    private string? TryGetFileUri()
    {
        try
        {
            if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return new Uri(doc.FilePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
        }
        return null;
    }
}
