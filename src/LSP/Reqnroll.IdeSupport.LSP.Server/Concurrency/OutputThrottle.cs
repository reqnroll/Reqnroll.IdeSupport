using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Concurrency;

/// <inheritdoc cref="IOutputThrottle"/>
public sealed class OutputThrottle : IOutputThrottle, IDisposable
{
    private readonly IIdeSupportLogger _logger;
    private readonly TimeProvider _time;

    // A plain Dictionary under _gate (not a ConcurrentDictionary): every decision below reads and writes several
    // fields of one State together, so the lock has to cover them anyway.
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private sealed class State
    {
        public bool HasRun;
        public DateTimeOffset LastStart;
        public Func<Task>? Pending;
        public ITimer? Timer;

        // Completes when the run in progress (if any) has finished. A placeholder rather than the action's own task,
        // so it is in place before the action starts and cannot be left set by an action that finishes synchronously.
        public Task? Running;
    }

    /// <summary>Initializes a new instance of the <see cref="OutputThrottle"/> class.</summary>
    public OutputThrottle(IIdeSupportLogger logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public Task RunThrottledAsync(string key, TimeSpan interval, Func<Task> action)
    {
        State state;
        TaskCompletionSource started;

        lock (_gate)
        {
            if (!_states.TryGetValue(key, out state!))
                _states[key] = state = new State();

            if (state.Pending is not null)
            {
                // A run is already waiting for its turn; it will use this newer action instead.
                state.Pending = action;
                return Task.CompletedTask;
            }

            var now = _time.GetUtcNow();
            var wait = state.HasRun ? state.LastStart + interval - now : TimeSpan.Zero;

            if (state.Running is not null || wait > TimeSpan.Zero)
            {
                state.Pending = action;
                ArmTimer(key, state, wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                return Task.CompletedTask;
            }

            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.HasRun = true;
            state.LastStart = now;
            state.Running = started.Task;
        }

        return RunAsync(state, started, action, logFailure: false);
    }

    /// <inheritdoc/>
    public void Cancel(string key)
    {
        lock (_gate)
        {
            if (!_states.Remove(key, out var state))
                return;

            state.Pending = null;
            state.Timer?.Dispose();
            state.Timer = null;
        }
    }

    /// <summary>Stops every pending timer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                state.Pending = null;
                state.Timer?.Dispose();
                state.Timer = null;
            }

            _states.Clear();
        }
    }

    // Called with _gate held.
    private void ArmTimer(string key, State state, TimeSpan due)
    {
        state.Timer?.Dispose();
        state.Timer = _time.CreateTimer(_ => _ = RunPendingAsync(key, state), null, due, Timeout.InfiniteTimeSpan);
    }

    private async Task RunPendingAsync(string key, State state)
    {
        try
        {
            // Never overlap the run that is still going: wait for it, then take whatever action is waiting by then.
            Task? running;
            lock (_gate)
                running = state.Running;
            if (running is not null)
                await running.ConfigureAwait(false);

            Func<Task>? action;
            TaskCompletionSource started;
            lock (_gate)
            {
                // Cancelled (and possibly replaced by a newer state) while waiting.
                if (!_states.TryGetValue(key, out var current) || !ReferenceEquals(current, state))
                    return;

                action = state.Pending;
                if (action is null)
                    return;

                state.Pending = null;
                state.Timer?.Dispose();
                state.Timer = null;
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                state.HasRun = true;
                state.LastStart = _time.GetUtcNow();
                state.Running = started.Task;
            }

            await RunAsync(state, started, action, logFailure: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[{key}] Throttled output failed: {ex.Message}");
        }
    }

    private async Task RunAsync(State state, TaskCompletionSource started, Func<Task> action, bool logFailure)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (logFailure)
        {
            _logger.LogWarning($"Throttled output failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.Running, started.Task))
                    state.Running = null;
            }

            started.TrySetResult();
        }
    }
}
