using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Concurrency;

/// <summary>
/// Debounces a per-project rescan action so a burst of rapid <c>.cs</c> edits (one per keystroke)
/// collapses into a single rescan after the edits settle, instead of running the (expensive,
/// whole-project) action on every keystroke.
/// </summary>
public interface IFeatureRescanDebouncer
{
    /// <summary>
    /// Schedules <paramref name="rescanAsync"/> to run for <paramref name="project"/> after the
    /// debounce window elapses. A call for the same project before the window elapses cancels the
    /// pending run and restarts the window, so only the last scheduled action for a project
    /// actually runs.
    /// </summary>
    void ScheduleRescan(LspReqnrollProject project, Func<CancellationToken, Task> rescanAsync);
}
