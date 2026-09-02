#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

/// <summary>
/// De-duplicates concurrent calls to <see cref="StepCodeLensService.GetLensesAsync"/> for the
/// same file, sharing one in-flight (or already-completed) <c>textDocument/codeLens</c> response
/// instead of letting every method-level lens in the file re-fetch it independently.
/// </summary>
/// <remarks>
/// <para>
/// VS.Extensibility's <c>ICodeLensProvider</c> is called once per method-level <c>CodeElement</c>
/// in a file, and every one of those calls wants the same whole-document response — without this,
/// a file with N step-definition methods triggers N redundant full <c>textDocument/codeLens</c>
/// round trips on every paint (issue #552 follow-up).
/// </para>
/// <para>
/// Mirrors <see cref="RunTestCodeLens.RunTestCodeLensResultCache"/>'s design (issue #491/#495) —
/// the same class of problem (VS.Extensibility calling once per code element, all wanting the same
/// shared result) got its fix there first, including the exactly-once-under-contention guarantee
/// a bare <c>ConcurrentDictionary.GetOrAdd</c> does NOT give: its factory can run more than once
/// concurrently on a cold key, so wrapping the real work in <see cref="Lazy{T}"/> is what actually
/// serialises it to one evaluation. Kept as its own type rather than generalising both into one
/// shared generic cache in the same change that adds this second consumer — a reasonable
/// follow-up, not attempted here to avoid touching that already-shipped, tested cache. Differs
/// from it in two ways: keyed by file alone (one <c>textDocument/codeLens</c> call already returns
/// every method's lens in that file, so there is no equivalent to its #495 per-line split), and no
/// runaway-work timeout (the fetch here is a single JSON-RPC round trip, not an unbounded
/// whole-corpus walk) — the constructor's <see cref="JoinableTaskFactory"/> parameter is still
/// required, though, since <see cref="AsyncLazy{T}"/> itself needs a real instance regardless of
/// whether the wrapped work touches the UI thread.
/// </para>
/// </remarks>
internal sealed class StepCodeLensResultCache
{
    /// <summary>Wraps the shared computation for one file — see this type's remarks on why <see cref="Lazy{T}"/> matters.</summary>
    private sealed class Entry
    {
        public Entry(AsyncLazy<IReadOnlyList<StepLensItem>> lazy) => Lazy = lazy;
        public AsyncLazy<IReadOnlyList<StepLensItem>> Lazy { get; }
    }

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<StepLensItem>>> _inner;
    private readonly JoinableTaskFactory _joinableTaskFactory;

    // Values are Lazy<Entry>, not Entry directly, for the same reason as
    // RunTestCodeLensResultCache: AddOrUpdate's factories may run more than once under
    // contention on the same key (several method lenses in the same file all missing the cache
    // at once is exactly this cache's core scenario), but only one factory result is ever
    // stored. A losing attempt's outer Lazy is simply discarded unevaluated, so its inner
    // CreateEntry — and the real fetch it would start — never runs at all.
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="inner">The real per-file fetch to wrap — typically <c>StepCodeLensService</c>'s own <c>textDocument/codeLens</c> call.</param>
    /// <param name="joinableTaskFactory">
    /// The <see cref="JoinableTaskFactory"/> each shared <see cref="AsyncLazy{T}"/> is constructed
    /// with (VS threading analyzers require a real instance, not <see langword="null"/>, even
    /// though <paramref name="inner"/> itself has no UI-thread affinity of its own). Defaults to
    /// <see cref="ThreadHelper.JoinableTaskFactory"/> — the real ambient VS one, which also works
    /// outside a real VS host (it lazily initializes against whatever thread first touches it) so
    /// tests don't need to supply anything special either. Overridable for symmetry with
    /// <see cref="RunTestCodeLens.RunTestCodeLensResultCache"/>'s equivalent parameter.
    /// </param>
    public StepCodeLensResultCache(
        Func<string, CancellationToken, Task<IReadOnlyList<StepLensItem>>> inner,
        JoinableTaskFactory? joinableTaskFactory = null)
    {
        _inner = inner;
        _joinableTaskFactory = joinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;
    }

    /// <summary>
    /// Returns the shared result for <paramref name="fileUri"/>, starting a new fetch only when
    /// none is in flight and no completed result is already cached. <paramref name="callerToken"/>
    /// only governs how long this particular call is willing to wait — per
    /// <see cref="AsyncLazy{T}.GetValueAsync(CancellationToken)"/>'s own documented contract, it
    /// never cancels the shared fetch itself, so one caller giving up can never abort the result
    /// every other method-level lens in the file is also waiting on.
    /// </summary>
    public async Task<IReadOnlyList<StepLensItem>> GetLensesAsync(string fileUri, CancellationToken callerToken)
    {
        var lazyEntry = _entries.AddOrUpdate(
            fileUri,
            addValueFactory: static (uri, self) => new Lazy<Entry>(() => self.CreateEntry(uri)),
            updateValueFactory: static (uri, existing, self) => self.IsUsable(existing.Value) ? existing : new Lazy<Entry>(() => self.CreateEntry(uri)),
            factoryArgument: this);

        var entry = lazyEntry.Value;
        return await entry.Lazy.GetValueAsync(callerToken).ConfigureAwait(false);
    }

    /// <summary>Drops the cached/in-flight result for <paramref name="fileUri"/>, if any.</summary>
    public void InvalidateFile(string fileUri) => _entries.TryRemove(fileUri, out _);

    /// <summary>Drops every cached/in-flight result.</summary>
    public void InvalidateAll() => _entries.Clear();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Entry CreateEntry(string fileUri)
    {
        // CancellationToken.None: this fetch is shared, not owned by whichever caller happened to
        // trigger it — see GetLensesAsync's remarks.
        var lazy = new AsyncLazy<IReadOnlyList<StepLensItem>>(() => _inner(fileUri, CancellationToken.None), _joinableTaskFactory);
        return new Entry(lazy);
    }

    private bool IsUsable(Entry entry)
    {
        if (!entry.Lazy.IsValueFactoryCompleted)
            return true; // still in flight — share it rather than starting a duplicate fetch.

        var task = entry.Lazy.GetValueAsync();
        // A successfully-completed result stays usable indefinitely: InvalidateFile/InvalidateAll
        // (wired from StepCodeLensState whenever the server actually reports a change) are the
        // real staleness signal, not a time-based TTL. Only a failed/cancelled result is unusable,
        // so a fresh caller gets a real retry instead of the same exception replayed forever.
        return !task.IsFaulted && !task.IsCanceled;
    }
}
