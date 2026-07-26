using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="IFeatureRescanDebouncer"/>
public sealed class FeatureRescanDebouncer : IFeatureRescanDebouncer, IDisposable
{
    private const int DebounceMilliseconds = 500;

    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly ConcurrentDictionary<LspReqnrollProject, CancellationTokenSource> _pending = new();

    /// <summary>Initializes a new instance of the <see cref="FeatureRescanDebouncer"/> class.</summary>
    public FeatureRescanDebouncer(IIdeSupportLogger logger, IOperationDurationRecorder? recorder = null)
    {
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Schedules <paramref name="rescanAsync"/> to run for the given project after a debounce delay, cancelling any rescan already pending for that project.</summary>
    public void ScheduleRescan(LspReqnrollProject project, Func<CancellationToken, Task> rescanAsync)
    {
        var newCts = new CancellationTokenSource();

        // Cancel and dispose any pending rescan for this project before replacing it -- only the
        // most recently scheduled action for a project should ever run.
        _pending.AddOrUpdate(project, newCts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return newCts;
        });

        _ = RunAfterDebounceAsync(project, rescanAsync, newCts);
    }

    private async Task RunAfterDebounceAsync(
        LspReqnrollProject project, Func<CancellationToken, Task> rescanAsync, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cts.Token).ConfigureAwait(false);

            // Performance Verification (Layer 4): time the debounced rescan that actually runs —
            // distinguishing "slow" from "wrong" the next time a cross-project binding issue is reported.
            using var _perf = _recorder.Measure(LspMethodNames.InternalFeatureRescan);
            await rescanAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal: a later edit superseded this scheduled rescan.
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[{project.ProjectName}] Debounced feature rescan failed: {ex.Message}");
        }
        finally
        {
            // Only remove our own entry -- a newer ScheduleRescan call may already have replaced it.
            _pending.TryRemove(new KeyValuePair<LspReqnrollProject, CancellationTokenSource>(project, cts));
            cts.Dispose();
        }
    }

    /// <summary>Cancels and disposes every still-pending debounce timer.</summary>
    public void Dispose()
    {
        // Claim each entry via TryRemove(key, ...) rather than iterating _pending.Values and
        // acting on whatever was there: RunAfterDebounceAsync's own finally block races this
        // method for the same CancellationTokenSource, and a snapshot-then-act loop could observe
        // an entry that finally already disposed by the time Cancel() runs here, throwing
        // ObjectDisposedException. Atomically removing by key means only one side ever wins the
        // entry -- the loser sees TryRemove return false and leaves that cts alone.
        foreach (var project in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(project, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
