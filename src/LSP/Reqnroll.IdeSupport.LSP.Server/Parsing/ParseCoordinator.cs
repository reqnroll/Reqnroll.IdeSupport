using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Parsing;

/// <inheritdoc cref="IParseCoordinator"/>
public sealed class ParseCoordinator : IParseCoordinator
{
    private readonly IIdeSupportLogger _logger;

    // Keyed by uri.ToString() (case-insensitive), matching IDocumentBufferService's own keying
    // convention rather than DocumentUri directly.
    //
    // A plain Dictionary under _gate, not a ConcurrentDictionary: deciding whether a URI is already running,
    // and recording the request when it is, must be one atomic step. (A ConcurrentDictionary.AddOrUpdate
    // invokes its factories outside the bucket lock, so two threads scheduling the same URI could both
    // decide "nothing is running" and start the work twice.) Reads and writes are cheap and happen once per
    // parse trigger, so a lock costs nothing measurable and is far easier to reason about.
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    // What is going on for one URI. At most one piece of work is running and at most one is waiting; the entry
    // exists only while one of them does.
    private sealed class State
    {
        // Completes when the running work has finished.
        public TaskCompletionSource RunningDone = NewCompletion();

        // The newest work that arrived while something was running, and the completion that everyone waiting for
        // "the latest work" holds. Newer work replaces older work in this slot and keeps the same completion: it
        // completes once the replacing work has run, which is what those waiters need.
        public Func<CancellationToken, Task>? Pending;
        public TaskCompletionSource? PendingDone;
    }

    /// <summary>Initializes a new instance of the <see cref="ParseCoordinator"/> class.</summary>
    public ParseCoordinator(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Schedule(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        var key = uri.ToString();
        State? startNow = null;

        lock (_gate)
        {
            if (_states.TryGetValue(key, out var state))
            {
                // Something is running for this URI. Only the newest request matters, so it takes the waiting
                // slot, replacing whatever was waiting there; the replaced work never runs. Deciding and
                // recording this under one lock is what makes "at most one running" hold when two Schedule
                // calls are themselves concurrent -- e.g. a didChange on the Serial dispatch lane arriving
                // while a BindingRegistryChanged reparse for the same file runs on a pool thread.
                if (state.Pending is not null)
                    _logger.LogVerbose($"[ParseCoordinator] '{uri}': newer work replaces the work that was waiting.");

                state.Pending = work;
                state.PendingDone ??= NewCompletion();
            }
            else
            {
                // Nothing running: start now, once the lock is released. The work itself must NOT start inside
                // the lock (it would hold up unrelated URIs for the duration of a parse), but callers rely on
                // the work's synchronous prefix still running inline, so a request arriving straight after a
                // didOpen/didChange on the Serial dispatch lane sees the freshly parsed buffer.
                startNow = new State();
                _states[key] = startNow;
            }
        }

        if (startNow is not null)
            _ = RunAsync(key, uri, startNow, work);
    }

    /// <inheritdoc/>
    public Task WaitForReadyAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        Task? waitFor = null;
        lock (_gate)
        {
            if (_states.TryGetValue(uri.ToString(), out var state))
                waitFor = (state.PendingDone ?? state.RunningDone).Task;
        }

        return waitFor is not null ? waitFor.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    // Runs the first piece of work inline (up to its first await), then whatever has been left in the waiting slot,
    // one at a time, until nothing is waiting.
    private async Task RunAsync(string key, DocumentUri uri, State state, Func<CancellationToken, Task> work)
    {
        while (true)
        {
            await RunSafelyAsync(uri, work).ConfigureAwait(false);

            TaskCompletionSource finished;
            Func<CancellationToken, Task>? next;
            lock (_gate)
            {
                finished = state.RunningDone;
                next = state.Pending;

                if (next is null)
                {
                    _states.Remove(key);
                }
                else
                {
                    state.Pending = null;
                    state.RunningDone = state.PendingDone!;
                    state.PendingDone = null;
                }
            }

            finished.TrySetResult();

            if (next is null)
                return;

            // Not inline: the work that was waiting starts on a pool thread, never on the one that finished the last.
            await Task.Yield();
            work = next;
        }
    }

    private async Task RunSafelyAsync(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        try
        {
            // CancellationToken.None: this work runs detached from whatever request triggered it (that
            // request, a Serial-lane notification, has already returned). A parse error here must not fault the
            // tracked task -- WaitForReadyAsync callers (FoldingRangeHandler, DocumentSymbolHandler) would
            // otherwise have that exception rethrown into their own request handling for a completely
            // unrelated failure.
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[ParseCoordinator] Scheduled work for '{uri}' failed: {ex.Message}");
        }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
