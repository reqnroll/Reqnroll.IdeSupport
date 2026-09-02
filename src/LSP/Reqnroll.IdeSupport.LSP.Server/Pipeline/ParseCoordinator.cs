using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="IParseCoordinator"/>
public sealed class ParseCoordinator : IParseCoordinator
{
    private readonly IIdeSupportLogger _logger;

    // Keyed by uri.ToString() (case-insensitive), matching IDocumentBufferService's own keying
    // convention rather than DocumentUri directly.
    //
    // A plain Dictionary under _gate, not a ConcurrentDictionary (issue #554): the previous
    // AddOrUpdate version could start the same URI's work twice. ConcurrentDictionary invokes its
    // add/update factories OUTSIDE the bucket lock, so two threads scheduling the same,
    // not-yet-pending URI both ran the add factory -- and that factory started the work rather
    // than returning something inert. Both parses ran concurrently (defeating the entire point of
    // this class) and only one of the two tasks was stored, leaving the other invisible to
    // WaitForReadyAsync. Reads and writes are both cheap and infrequent (one per parse trigger),
    // so a lock costs nothing measurable and is far easier to reason about.
    private readonly Dictionary<string, Task> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="ParseCoordinator"/> class.</summary>
    public ParseCoordinator(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Schedule(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        var key = uri.ToString();
        Task scheduled;
        TaskCompletionSource? startNow = null;

        lock (_gate)
        {
            // Chain onto any already-pending work for this URI so two scheduled operations for the
            // same file never run concurrently -- mirrors RefreshDebouncer's per-key coordination,
            // but chains (runs after) instead of cancels-and-replaces, since every scheduled parse
            // still needs to happen (unlike a debounced refresh, where only the last trigger matters).
            // Deciding and publishing under one lock is what makes that guarantee hold when the two
            // Schedule calls are themselves concurrent -- e.g. a didChange on the Serial dispatch
            // lane arriving while a BindingRegistryChanged reparse for the same file runs on a pool
            // thread, the shape that produced #554's overlapping parses.
            if (_pending.TryGetValue(key, out var previous))
            {
                scheduled = previous.ContinueWith(
                    _ => RunSafelyAsync(uri, work),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
            else
            {
                // Nothing pending: publish a placeholder now and start the work below, once the
                // lock is released. Publishing before starting is what closes the race -- a second
                // Schedule arriving mid-parse finds this entry and chains onto it instead of
                // starting its own run. The work itself must NOT start inside the lock (it would
                // hold up unrelated URIs for the duration of a parse), and equally must not be
                // pushed onto the thread pool: callers rely on the work's synchronous prefix still
                // running inline, so a request arriving straight after a didOpen/didChange on the
                // Serial dispatch lane sees the freshly parsed buffer.
                startNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduled = startNow.Task;
            }

            _pending[key] = scheduled;
        }

        if (startNow is not null)
        {
            var running = RunSafelyAsync(uri, work);
            if (running.IsCompleted)
                startNow.TrySetResult();
            else
                _ = running.ContinueWith(
                    _ => startNow.TrySetResult(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        // Remove our own entry once it completes, but only if a newer Schedule call for this URI
        // hasn't already replaced it in the meantime -- same TryRemove(key, expectedValue) race
        // guard used by ConnectorBindingRegistryProvider/RefreshDebouncer elsewhere in this codebase.
        _ = scheduled.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, t))
                        _pending.Remove(key);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc/>
    public Task WaitForReadyAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_gate)
            _pending.TryGetValue(uri.ToString(), out pending);

        return pending is not null ? pending.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    private async Task RunSafelyAsync(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        try
        {
            // CancellationToken.None: this work now runs detached from whatever request
            // triggered it (that request, a Serial-lane notification, has already returned).
            // A parse error here must not fault the tracked Task -- WaitForReadyAsync callers
            // (FoldingRangeHandler, DocumentSymbolHandler) would otherwise have that exception
            // rethrown into their own request handling for a completely unrelated failure.
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[ParseCoordinator] Scheduled work for '{uri}' failed: {ex.Message}");
        }
    }
}
