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
/// De-duplicates concurrent calls to <see cref="RunTestCodeLensService.GetTargetsForLineAsync"/> for
/// the same <c>(fileUri, line)</c>, sharing one in-flight (or already-completed) computation instead
/// of letting every caller re-resolve the same line independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this fixes (live report, 2026-08-26; re-scoped per issue #495):</b>
/// <c>RunTestCodeLensService</c> used to have a single <c>GetTargetsAsync(fileUri)</c> that resolved
/// <i>every</i> scenario in a <c>.feature</c> file on every call — called from two independent
/// places that both went through one shared delegate: <c>RunTestCodeLensTaggerProvider</c>
/// (in-process, once per tagger refresh) and, for every <i>visible Scenario line's own</i>
/// <c>RunTestCodeLensDataPoint.GetDataAsync</c>, an out-of-process ServiceHub callback that only
/// ever used its own line's entry out of the whole-file result. On a large feature file with N
/// visible scenario lines, that meant N+1 concurrent full-document walks, each redoing all of it
/// from scratch — confirmed live: with #491's cache already making each
/// <c>reqnroll/resolveTestTargets</c> call ~4ms instead of ~150ms, a single walk of ~1,300 scenarios
/// still took long enough — repeated 5x concurrently — that individual <c>GetDataAsync</c> calls hit
/// VS's own classic-CodeLens timeout (observed at ~26s) and were cancelled.
/// </para>
/// <para>
/// Issue #495 split the whole-file walk in two: <c>RunTestCodeLensService.GetTagLocationsAsync</c>
/// (symbol tree only, no <c>resolveTestTargets</c> calls, used by the tagger to place tags) and
/// <c>GetTargetsForLineAsync(fileUri, line)</c> (one <c>resolveTestTargets</c> call for exactly the
/// line a data point needs). This cache now de-dupes the latter, keyed by <c>(fileUri, line)</c>
/// instead of just <c>fileUri</c> — concurrent visible lines in the same file no longer contend on
/// one shared computation, since each now only does its own line's (already cheap) work.
/// </para>
/// <para>
/// <b>A completed result is reused until an explicit invalidation, not a time-based TTL.</b> An
/// earlier version of this cache expired a completed result after a fixed few seconds, on the
/// theory that a short TTL would only need to absorb one initial burst of near-simultaneous
/// <c>GetDataAsync</c> calls. In practice (live report, 2026-08-26) a single walk on a large corpus
/// (~2,400 scenarios) can itself take 30-45 seconds — far longer than the TTL — so scrolling to a
/// newly-visible region more than a few seconds after the last completed walk discarded a perfectly
/// good result and forced an entirely new one, which then reliably outlived every individual
/// caller's own VS-imposed timeout. There is no correctness reason for a TTL here: real staleness is
/// already reported explicitly via <see cref="InvalidateFile"/>/<see cref="InvalidateAll"/>, called
/// from <c>CodeLensRefreshInterceptor</c> whenever the server's <c>reqnroll/refreshCodeLens</c>
/// notification says the underlying binding registry or feature file actually changed. A completed
/// result is therefore valid indefinitely until one of those fires.
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
    /// <summary>One line's worth of shared computation — keyed by file plus line since #495, not file alone.</summary>
    internal readonly record struct Key(string FileUri, int Line);

    private sealed class Entry
    {
        public Entry(CancellationTokenSource cts, AsyncLazy<IReadOnlyList<RunTestTargetEntry>> lazy)
        {
            Cts = cts;
            Lazy = lazy;
        }

        public CancellationTokenSource Cts { get; }
        public AsyncLazy<IReadOnlyList<RunTestTargetEntry>> Lazy { get; }
    }

    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>> _inner;
    private readonly ILogger<RunTestCodeLensResultCache> _logger;
    private readonly JoinableTaskFactory _joinableTaskFactory;
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
    private readonly ConcurrentDictionary<Key, Lazy<Entry>> _entries = new();

    /// <param name="inner">The real per-line resolver to wrap — typically <see cref="RunTestCodeLensService.GetTargetsForLineAsync"/>.</param>
    /// <param name="logger">Logging sink.</param>
    /// <param name="joinableTaskFactory">
    /// The <see cref="JoinableTaskFactory"/> each shared <see cref="AsyncLazy{T}"/> is constructed
    /// with. Defaults to <see cref="ThreadHelper.JoinableTaskFactory"/> — the real ambient VS one,
    /// needed because <c>RunTestCodeLensService.GetTargetsForLineAsync</c>'s own call chain switches
    /// to the UI thread. Overridable so unit tests can supply a standalone
    /// <c>new JoinableTaskContext().Factory</c> instead of depending on a real VS host.
    /// </param>
    /// <param name="computationTimeout">Upper bound on one shared computation's own lifetime, independent of any caller's token — a safety net against a truly runaway resolution, not the mechanism callers use to give up waiting. Defaults to 60 seconds, comfortably above VS's own observed ~26-second per-data-point timeout so the shared work outlives any single caller that gives up on it.</param>
    public RunTestCodeLensResultCache(
        Func<string, int, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>> inner,
        ILogger<RunTestCodeLensResultCache> logger,
        JoinableTaskFactory? joinableTaskFactory = null,
        TimeSpan? computationTimeout = null)
    {
        _inner = inner;
        _logger = logger;
        _joinableTaskFactory = joinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;
        _computationTimeout = computationTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Returns the shared result for <paramref name="fileUri"/>:<paramref name="line"/>, starting a
    /// new computation only when none is in flight and no completed result is already cached.
    /// <paramref name="callerToken"/> only governs how long this particular call is willing to wait
    /// — it never cancels the shared computation itself (see this type's remarks).
    /// </summary>
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsAsync(string fileUri, int line, CancellationToken callerToken)
    {
        var key = new Key(fileUri, line);
        var lazyEntry = _entries.AddOrUpdate(
            key,
            addValueFactory: static (k, self) => new Lazy<Entry>(() => self.CreateEntry(k)),
            updateValueFactory: static (k, existing, self) => self.IsUsable(existing.Value) ? existing : new Lazy<Entry>(() => self.CreateEntry(k)),
            factoryArgument: this);

        var entry = lazyEntry.Value;
        return await entry.Lazy.GetValueAsync(callerToken).ConfigureAwait(false);
    }

    /// <summary>Drops every cached/in-flight result for every line of <paramref name="fileUri"/>, cancelling each computation.</summary>
    public void InvalidateFile(string fileUri)
    {
        foreach (var key in _entries.Keys.Where(k => k.FileUri == fileUri).ToList())
            InvalidateKey(key);
    }

    /// <summary>Drops every cached/in-flight result, cancelling each computation.</summary>
    public void InvalidateAll()
    {
        foreach (var key in _entries.Keys.ToList())
            InvalidateKey(key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void InvalidateKey(Key key)
    {
        if (_entries.TryRemove(key, out var lazyEntry) && lazyEntry.IsValueCreated)
            lazyEntry.Value.Cts.Cancel();
    }

    private Entry CreateEntry(Key key)
    {
        var cts = new CancellationTokenSource(_computationTimeout);
        var lazy = new AsyncLazy<IReadOnlyList<RunTestTargetEntry>>(() => RunAsync(key, cts.Token), _joinableTaskFactory);
        return new Entry(cts, lazy);
    }

    private bool IsUsable(Entry entry)
    {
        if (!entry.Lazy.IsValueFactoryCompleted)
            return true; // still in flight — share it rather than starting a duplicate resolution.

        var task = entry.Lazy.GetValueAsync();
        // A successfully-completed result stays usable indefinitely — see this type's remarks on
        // why a time-based TTL isn't needed: InvalidateFile/InvalidateAll are the real staleness
        // signal. Only a failed/cancelled result is treated as unusable, so a fresh caller gets a
        // real retry instead of the same exception replayed forever.
        return !task.IsFaulted && !task.IsCanceled;
    }

    private async Task<IReadOnlyList<RunTestTargetEntry>> RunAsync(Key key, CancellationToken ct)
    {
        _logger.LogInformation("RunTestCodeLensResultCache: starting shared computation for {FileUri}:{Line}", key.FileUri, key.Line);
        var result = await _inner(key.FileUri, key.Line, ct).ConfigureAwait(false);
        _logger.LogInformation("RunTestCodeLensResultCache: shared computation for {FileUri}:{Line} completed with {Count} entr{Suffix}",
            key.FileUri, key.Line, result.Count, result.Count == 1 ? "y" : "ies");
        return result;
    }
}
