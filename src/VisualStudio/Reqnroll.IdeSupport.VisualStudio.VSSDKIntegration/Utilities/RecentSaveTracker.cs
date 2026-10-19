#nullable enable

using System;
using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.VisualStudio.Utilities;

/// <summary>
/// Remembers when each file last began a save, so a format request that an <i>automatic</i> trigger
/// (a format-on-save extension, Code Cleanup on save) fires from the save sequence can be told apart
/// from an explicit Format Document. A rejection of such an automatic format must be
/// logged, never shown in a dialog.
/// </summary>
/// <remarks>
/// <para>
/// Fed by <c>IVsRunningDocTableEvents3.OnBeforeSave</c> (see the Extension project's
/// <c>SaveTrackingRdtListener</c>). Deliberately records only the <i>start</i> of a save and asks
/// "did one begin within the last <see cref="DefaultWindow"/>?" rather than tracking an in-progress
/// interval: the format request is asynchronous, so its rejection arrives after the save has already
/// completed, and RDT listeners are called in no guaranteed order, so a format-on-save handler may run
/// just before ours within the same dispatch. A window covers both. The cost is that an explicit
/// format within a few seconds of a save beginning also gets no dialog — rare and harmless.
/// </para>
/// <para>Keyed by file path, compared case-insensitively. Thread-safe.</para>
/// </remarks>
public sealed class RecentSaveTracker
{
    /// <summary>How recently a save must have begun for a format rejection to count as automatic.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    /// <summary>The tracker shared by the RDT listener (writer) and the format command filter (reader).</summary>
    public static RecentSaveTracker Instance { get; } = new();

    private readonly ConcurrentDictionary<string, DateTime> _lastSaveBegan =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTime> _utcNow;

    /// <summary>Creates a tracker; <paramref name="utcNow"/> is a test seam for the clock.</summary>
    public RecentSaveTracker(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>A save of <paramref name="path"/> is beginning.</summary>
    public void SaveBegan(string path)
    {
        if (!string.IsNullOrEmpty(path))
            _lastSaveBegan[path] = _utcNow();
    }

    /// <summary>
    /// True if a save of <paramref name="path"/> began within the last <paramref name="window"/>
    /// (default <see cref="DefaultWindow"/>).
    /// </summary>
    public bool SavedRecently(string path, TimeSpan? window = null) =>
        !string.IsNullOrEmpty(path)
        && _lastSaveBegan.TryGetValue(path, out var began)
        && _utcNow() - began <= (window ?? DefaultWindow);
}
