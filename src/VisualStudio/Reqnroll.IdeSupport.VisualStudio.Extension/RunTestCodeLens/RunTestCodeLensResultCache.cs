#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RunTestCodeLens;

/// <summary>
/// De-duplicates concurrent calls to <see cref="RunTestCodeLensService.GetTargetsAsync"/> for the
/// same file, sharing one in-flight (or recently-completed) computation instead of letting every
/// caller re-walk the whole document independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this fixes (live report, 2026-08-26):</b> <see cref="RunTestCodeLensService.GetTargetsAsync"/>
/// resolves <i>every</i> scenario in a <c>.feature</c> file, not just one line's worth. It's called
/// from two independent places that both go through the single <see cref="RunTestCodeLensRedirect.GetTargetsAsync"/>
/// delegate: <c>RunTestCodeLensTaggerProvider</c> (in-process, once per tagger refresh) and, for
/// every <i>visible Scenario line's own</i> <c>RunTestCodeLensDataPoint.GetDataAsync</c>, an
/// out-of-process ServiceHub callback. On a large feature file with N visible scenario lines, that
/// meant N+1 concurrent full-document walks competing for the same LSP server, each redoing all of
/// it from scratch. Confirmed live: with #491's cache already making each
/// <c>reqnroll/resolveTestTargets</c> call ~4ms instead of ~150ms, a single walk of ~1,300 scenarios
/// still took long enough — repeated 5x concurrently — that individual <c>GetDataAsync</c> calls hit
/// VS's own classic-CodeLens timeout (observed at ~26s) and were cancelled
/// (<see cref="OperationCanceledException"/> from <c>RunTestCodeLensService</c>'s own
/// <c>cancellationToken.ThrowIfCancellationRequested()</c>), leaving the lens stuck showing "Loading
/// data..." forever with no working Run/Debug popup.
/// </para>
/// <para>
/// <b>Cancellation is deliberately decoupled from any one caller.</b> Each caller supplies its own
/// token — for the OOP data points, that's VS's own per-request timeout token. Backed by
/// <see cref="AsyncLazy{T}"/> specifically because its documented contract is exactly this: the
/// token passed to <see cref="AsyncLazy{T}.GetValueAsync(CancellationToken)"/> only cancels that
/// caller's own wait, never the underlying value factory once it has started, so one caller's
/// timeout can never abort the shared work for every other waiter. Each shared computation is
/// additionally bounded by its own independent <see cref="CancellationTokenSource"/> (<paramref
/// name="computationTimeout"/>) as a runaway-work safety net, unrelated to any single request's
/// deadline.
/// </para>
/// </remarks>
internal sealed class RunTestCodeLensResultCache
{
    private sealed class Entry
    {
        public Entry(CancellationTokenSource cts, AsyncLazy<IReadOnlyList<RunTestTargetEntry>> lazy)
        {
            Cts = cts;
            Lazy = lazy;
        }

        public CancellationTokenSource Cts { get; }
        public AsyncLazy<IReadOnlyList<RunTestTargetEntry>> Lazy { get; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>> _inner;
    private readonly ILogger<RunTestCodeLensResultCache> _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly TimeSpan _resultTtl;
    private readonly TimeSpan _computationTimeout;

    // Values are Lazy<Entry>, not Entry directly: ConcurrentDictionary.AddOrUpdate's factories may
    // run more than once under contention on the same key (multiple CodeLens lines in the same file
    // all missing the cache at once is exactly this cache's core scenario), but only one factory
    // result is ever stored. A bare Entry factory would allocate a real CancellationTokenSource +
    // AsyncLazy for every losing attempt too, leaking their timers until GC. Wrapping construction
    // in Lazy<Entry> means a losing attempt's outer Lazy is simply discarded unevaluated — its inner
    // CreateEntry (and the CTS it allocates) never runs at all, so there is nothing to leak or
    // dispose. CreateEntry itself does no I/O, so a concurrent .Value read blocking on Lazy's default
    // execute-once lock is effectively instantaneous, not a real contention concern.
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries = new(StringComparer.Ordinal);

    /// <param name="inner">The real (expensive, whole-document) resolver to wrap — typically <see cref="RunTestCodeLensService.GetTargetsAsync"/>.</param>
    /// <param name="logger">Logging sink.</param>
    /// <param name="joinableTaskFactory">
    /// The <see cref="JoinableTaskFactory"/> each shared <see cref="AsyncLazy{T}"/> is constructed
    /// with. Defaults to <see cref="ThreadHelper.JoinableTaskFactory"/> — the real ambient VS one,
    /// needed because <c>RunTestCodeLensService.GetTargetsAsync</c>'s own call chain switches to
    /// the UI thread. Overridable so unit tests can supply a standalone
    /// <c>new JoinableTaskContext().Factory</c> instead of depending on a real VS host.
    /// </param>
    /// <param name="resultTtl">How long a completed result stays reusable by a new caller before a fresh computation is started. Defaults to 3 seconds — long enough to absorb a burst of near-simultaneous CodeLens data-point calls, short enough that a real edit's invalidation (see <see cref="InvalidateFile"/>) is rarely even needed to see fresh results.</param>
    /// <param name="computationTimeout">Upper bound on one shared computation's own lifetime, independent of any caller's token — a safety net against a truly runaway resolution, not the mechanism callers use to give up waiting. Defaults to 60 seconds, comfortably above VS's own observed ~26-second per-data-point timeout so the shared work outlives any single caller that gives up on it.</param>
    public RunTestCodeLensResultCache(
        Func<string, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>> inner,
        ILogger<RunTestCodeLensResultCache> logger,
        JoinableTaskFactory? joinableTaskFactory = null,
        TimeSpan? resultTtl = null,
        TimeSpan? computationTimeout = null)
    {
        _inner = inner;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;
        _resultTtl = resultTtl ?? TimeSpan.FromSeconds(3);
        _computationTimeout = computationTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Returns the shared result for <paramref name="fileUri"/>, starting a new computation only
    /// when none is in flight and no recent-enough one is cached. <paramref name="callerToken"/>
    /// only governs how long this particular call is willing to wait — it never cancels the shared
    /// computation itself (see this type's remarks).
    /// </summary>
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsAsync(string fileUri, CancellationToken callerToken)
    {
        var lazyEntry = _entries.AddOrUpdate(
            fileUri,
            addValueFactory: uri => new Lazy<Entry>(() => CreateEntry(uri)),
            updateValueFactory: (uri, existing) => IsUsable(existing.Value) ? existing : new Lazy<Entry>(() => CreateEntry(uri)));

        var entry = lazyEntry.Value;
        var result = await entry.Lazy.GetValueAsync(callerToken).ConfigureAwait(false);
        entry.CompletedAtUtc ??= DateTime.UtcNow;
        return result;
    }

    /// <summary>Drops the cached/in-flight result for <paramref name="fileUri"/>, if any, cancelling its computation.</summary>
    public void InvalidateFile(string fileUri)
    {
        if (_entries.TryRemove(fileUri, out var lazyEntry) && lazyEntry.IsValueCreated)
            lazyEntry.Value.Cts.Cancel();
    }

    /// <summary>Drops every cached/in-flight result, cancelling each computation.</summary>
    public void InvalidateAll()
    {
        foreach (var fileUri in _entries.Keys.ToList())
            InvalidateFile(fileUri);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Entry CreateEntry(string fileUri)
    {
        var cts = new CancellationTokenSource(_computationTimeout);
        var lazy = new AsyncLazy<IReadOnlyList<RunTestTargetEntry>>(() => RunAsync(fileUri, cts.Token), _joinableTaskFactory);
        return new Entry(cts, lazy);
    }

    private bool IsUsable(Entry entry)
    {
        if (!entry.Lazy.IsValueFactoryCompleted)
            return true; // still in flight — share it rather than starting a duplicate walk.

        var task = entry.Lazy.GetValueAsync();
        if (task.IsFaulted || task.IsCanceled)
            return false; // don't hand a failed/cancelled result to a fresh caller.

        return entry.CompletedAtUtc is { } completedAt && DateTime.UtcNow - completedAt < _resultTtl;
    }

    private async Task<IReadOnlyList<RunTestTargetEntry>> RunAsync(string fileUri, CancellationToken ct)
    {
        _logger.LogInformation("RunTestCodeLensResultCache: starting shared computation for {FileUri}", fileUri);
        var result = await _inner(fileUri, ct).ConfigureAwait(false);
        _logger.LogInformation("RunTestCodeLensResultCache: shared computation for {FileUri} completed with {Count} entr{Suffix}",
            fileUri, result.Count, result.Count == 1 ? "y" : "ies");
        return result;
    }
}
