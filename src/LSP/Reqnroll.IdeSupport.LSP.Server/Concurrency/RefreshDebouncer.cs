using System.Collections.Concurrent;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Concurrency;

/// <inheritdoc cref="IRefreshDebouncer"/>
public sealed class RefreshDebouncer : IRefreshDebouncer, IDisposable
{
    private readonly IIdeSupportLogger _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    /// <summary>Initializes a new instance of the <see cref="RefreshDebouncer"/> class.</summary>
    public RefreshDebouncer(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Schedule(string key, TimeSpan delay, Func<CancellationToken, Task> action)
    {
        var newCts = new CancellationTokenSource();

        // Cancel and dispose any pending run for this key before replacing it -- only the most
        // recently scheduled action for a key should ever run.
        _pending.AddOrUpdate(key, newCts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return newCts;
        });

        _ = RunAfterDelayAsync(key, delay, action, newCts);
    }

    private async Task RunAfterDelayAsync(
        string key, TimeSpan delay, Func<CancellationToken, Task> action, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal: a later trigger superseded this scheduled run.
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[{key}] Debounced refresh failed: {ex.Message}");
        }
        finally
        {
            // Only remove our own entry -- a newer Schedule call may already have replaced it.
            _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            cts.Dispose();
        }
    }

    /// <summary>Cancels and disposes every still-pending debounce timer.</summary>
    public void Dispose()
    {
        // Claim each entry via TryRemove(key, ...) rather than iterating _pending.Values and
        // acting on whatever was there: RunAfterDelayAsync's own finally block races this method
        // for the same CancellationTokenSource, and a snapshot-then-act loop could observe an
        // entry that finally already disposed by the time Cancel() runs here, throwing
        // ObjectDisposedException. Atomically removing by key means only one side ever wins the
        // entry -- the loser sees TryRemove return false and leaves that cts alone.
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
