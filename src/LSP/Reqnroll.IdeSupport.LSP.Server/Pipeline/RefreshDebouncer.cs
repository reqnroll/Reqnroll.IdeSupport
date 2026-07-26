using System.Collections.Concurrent;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

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
        foreach (var cts in _pending.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pending.Clear();
    }
}
