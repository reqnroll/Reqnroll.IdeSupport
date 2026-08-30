#nullable disable
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Visual Studio's MEF-exported <see cref="IIdeScope"/>/<see cref="IVsIdeScope"/> implementation:
/// the solution-wide root that resolves and caches per-project <see cref="VsProjectScope"/>
/// instances and exposes DTE, logging, telemetry, and file-system access to the rest of the
/// integration.
/// </summary>
[Export(typeof(IIdeScope))]
[Export(typeof(IVsIdeScope))]
public class VsIdeScope : IVsIdeScope
{
    private readonly CancellationTokenSource _backgroundTaskTokenSource = new();
    private readonly ConcurrentDictionary<string, VsProjectScope>
        _projectScopes = new(StringComparer.OrdinalIgnoreCase);

    private bool _activityStarted;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public VsIdeScope([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
        ITelemetryService telemetryService,
        IFileSystemForIDE fileSystem,
        Reqnroll.IdeSupport.VisualStudio.Logging.IdeSupportCompositeLogger compositeLogger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Logger = compositeLogger;
        ServiceProvider = serviceProvider;
        TelemetryService = telemetryService;
        FileSystem = fileSystem;

        Dte = (DTE) serviceProvider.GetService(typeof(DTE));

        Logger.LogVerbose("Creating IDE Scope");

        IsSolutionLoaded = Dte.Solution.IsOpen;
    }

    /// <summary>The VS service provider used to resolve VS SDK services.</summary>
    public IServiceProvider ServiceProvider { get; }
    /// <summary>The DTE automation object for the current VS instance.</summary>
    public DTE Dte { get; }

    /// <summary>Whether a solution was open at the time this scope was created.</summary>
    public bool IsSolutionLoaded { get; private set; }

    /// <summary>The composite logger shared across the VSSDK integration.</summary>
    public IIdeSupportLogger Logger { get; }
    /// <summary>The telemetry service used to report usage/error events.</summary>
    public ITelemetryService TelemetryService { get; }
    /// <summary>IDE-level actions available to callers; currently unset.</summary>
    public IIdeActions Actions { get; }
    /// <summary>The IDE's file system abstraction.</summary>
    public IFileSystemForIDE FileSystem { get; }

    /// <summary>Runs <paramref name="action"/> fire-and-forget, routing exceptions through <paramref name="onException"/> and telemetry.</summary>
    public void FireAndForget(Func<Task> action, Action<Exception> onException,
        [CallerMemberName] string callerName = "???")
    {
        action().FileAndForget($"vs/Reqnroll/{nameof(FireAndForget)}/{callerName}",
            "Error on a background task in Reqnroll",
            exception =>
            {
                Logger.LogException(TelemetryService, exception, $"Called from {callerName}");
                onException(exception);
                return true;
            });
    }

    /// <summary>Runs <paramref name="action"/> on a dedicated long-running background thread, logging any exception through telemetry.</summary>
    public void FireAndForgetOnBackgroundThread(Func<CancellationToken, Task> action,
        [CallerMemberName] string callerName = "???")
    {
        _ = Task.Factory.StartNew(async () =>
            {
                try
                {
                    ThreadHelper.ThrowIfOnUIThread(callerName);
                    await action(_backgroundTaskTokenSource.Token);
                }
                catch (Exception e)
                {
                    Logger.LogException(TelemetryService, e, $"Called from {callerName}");
                }
            },
            _backgroundTaskTokenSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    /// <summary>Switches to the UI thread and runs <paramref name="action"/> there.</summary>
    public async Task RunOnUiThreadAsync(Action action)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        action();
    }

    /// <summary>Cancels and disposes the background-task cancellation source.</summary>
    public void Dispose()
    {
        _backgroundTaskTokenSource.Cancel();
        _backgroundTaskTokenSource.Dispose();
    }

    /// <summary>
    /// Resolves the <see cref="IProjectScope"/> for <paramref name="project"/>, creating and
    /// caching a new <see cref="VsProjectScope"/> on first access; returns a <c>VoidProjectScope</c>
    /// for null or non-solution projects.
    /// </summary>
    public IProjectScope GetProjectScope(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project == null ||
            !VsUtils.IsSolutionProject(project))
            return new VoidProjectScope(this);

        var projectId = GetProjectId(project);
        var projectScope = _projectScopes.GetOrAdd(projectId, id => CreateProjectScope(id, project));
        return projectScope;
    }

    private void OnActivityStarted()
    {
        if (_activityStarted)
            return;

        _activityStarted = true;
    }

    private string GetProjectId(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return project.FullName;
    }

    private VsProjectScope CreateProjectScope(string id, Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        OnActivityStarted();
        Logger.LogInfo($"Initializing project: {project.Name}");
        var projectScope = new VsProjectScope(id, project, this);
        projectScope.InitializeServices();
        return projectScope;
    }

    private bool HasFeatureFiles(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!VsUtils.IsSolutionProject(project))
                return false;
            return VsUtils.GetPhysicalFileProjectItems(project)
                .Any(pi => FileSystemHelper.IsOfType(VsUtils.GetFilePath(pi), ".feature"));
        }
        catch (Exception e)
        {
            Logger.LogDebugException(e);
            return false;
        }
    }
}
