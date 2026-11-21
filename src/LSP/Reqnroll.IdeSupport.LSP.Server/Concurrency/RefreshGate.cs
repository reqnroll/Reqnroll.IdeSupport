using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Concurrency;

/// <summary>
/// Keeps at most one server-to-client refresh request of each kind outstanding, so a client that answers slowly is
/// not buried under requests it has not got to yet.
/// </summary>
/// <remarks>
/// <para>
/// <c>workspace/semanticTokens/refresh</c> and <c>workspace/inlayHint/refresh</c> are requests: the server learns when
/// the client has dealt with each one. Sending one after every pause in typing, without looking at whether the previous
/// one has been answered, lets them pile up when the client is slow (Visual Studio in a very large feature file was
/// seen answering after a median of 14 s, up to 87 s, with about 30 outstanding at once). Every one of them is work the
/// client has to do, and the ones queued behind the first are for states that are already out of date.
/// </para>
/// <para>
/// A request that arrives while one is outstanding is not sent; it is remembered, and exactly one more request goes out
/// as soon as the outstanding one has been answered. The client is therefore never more than one refresh behind, and it
/// is told about the latest change as soon as it is ready for it.
/// </para>
/// <para>
/// Like <see cref="IRefreshDebouncer"/> this state lives in a singleton: MediatR notification handlers are transient.
/// </para>
/// </remarks>
public interface IRefreshGate
{
    /// <summary>
    /// Runs <paramref name="send"/> for <paramref name="key"/> unless a run for that key is still outstanding, in which
    /// case <paramref name="send"/> runs once more when it ends. The returned task completes when this call's work is
    /// done: after the outstanding run if this call started one, immediately if it was folded into one.
    /// </summary>
    Task RunAsync(string key, Func<CancellationToken, Task> send);
}

/// <inheritdoc cref="IRefreshGate"/>
public sealed class RefreshGate : IRefreshGate
{
    /// <summary>How long an unanswered request holds the gate before it is given up on, so a client that never answers cannot stop refreshes for good.</summary>
    public static readonly TimeSpan OutstandingTimeout = TimeSpan.FromSeconds(60);

    private readonly IIdeSupportLogger _logger;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private sealed class State
    {
        public bool Outstanding;

        // The latest request that arrived while one was outstanding; it is the one that runs when that one ends.
        public Func<CancellationToken, Task>? Next;
    }

    /// <summary>Initializes a new instance of the <see cref="RefreshGate"/> class.</summary>
    public RefreshGate(IIdeSupportLogger logger, TimeSpan? timeout = null)
    {
        _logger = logger;
        _timeout = timeout ?? OutstandingTimeout;
    }

    /// <inheritdoc/>
    public async Task RunAsync(string key, Func<CancellationToken, Task> send)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
                _states[key] = state = new State();

            if (state.Outstanding)
            {
                state.Next = send;
                _logger.LogVerbose($"RefreshGate[{key}]: a request is still outstanding; one more will follow its answer.");
                return;
            }

            state.Outstanding = true;
            state.Next = null;
        }

        while (true)
        {
            using var cancellation = new CancellationTokenSource();
            try
            {
                var request = send(cancellation.Token);

                // Cancelling a refresh request does not end it: the task completes only when the client answers (or
                // fails). So the wait is raced against a timer, and the token is cancelled as well so that a request
                // that can be abandoned is.
                var giveUp = Task.Delay(_timeout, cancellation.Token);
                var first = await Task.WhenAny(request, giveUp).ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);

                if (first == request)
                {
                    await request.ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning($"RefreshGate[{key}]: no answer after {_timeout.TotalSeconds:0} s; the gate is released.");
                    _ = request.ContinueWith(
                        static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                // The sender reports its own failures; this only makes sure the gate is released.
                _logger.LogVerbose($"RefreshGate[{key}]: request ended with {ex.GetType().Name}: {ex.Message}");
            }

            lock (_gate)
            {
                var state = _states[key];
                if (state.Next is null)
                {
                    state.Outstanding = false;
                    return;
                }

                send = state.Next;
                state.Next = null;
            }
        }
    }
}
