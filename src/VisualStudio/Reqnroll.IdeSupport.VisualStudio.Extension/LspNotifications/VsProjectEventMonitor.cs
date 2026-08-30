using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;

/// <summary>
/// Monitors DTE solution and build events, and per-item add/remove/rename events via
/// <see cref="IVsTrackProjectDocumentsEvents2"/>, and sends <c>reqnroll/projectLoaded</c>,
/// <c>reqnroll/projectUnloaded</c>, and <c>reqnroll/projectFiles</c> notifications to the
/// LSP server via <see cref="LspInterceptingPipe"/>.
/// </summary>
/// <remarks>
/// Created by <see cref="ReqnrollLanguageClient"/> after the server has initialised
/// successfully.  Holds strong references to DTE event sinks — required because DTE
/// event objects are COM and are released if only a weak reference is held.
///
/// Solution Explorer renames/adds/removes of a single feature or binding file do not raise
/// any <see cref="SolutionEvents"/> or <see cref="BuildEvents"/> — those only cover whole
/// projects and full builds. Without <see cref="IVsTrackProjectDocumentsEvents2"/>, a rename
/// left the server's file-membership index (<c>reqnroll/projectFiles</c>) pointing at the old
/// path until the next full build or solution reload, so the renamed file appeared unowned
/// (no diagnostics, no step rename) in the meantime (issue #32).
/// </remarks>
internal sealed class VsProjectEventMonitor : IDisposable, IVsTrackProjectDocumentsEvents2
{
    private readonly LspInterceptingPipe    _pipe;
    private readonly ILogger<VsProjectEventMonitor> _logger;
    private readonly DTE2                   _dte;
    private readonly IServiceProvider       _serviceProvider;
    private readonly DocumentActivationState _activationState;

    // DTE event sinks — must be kept alive as fields.
    private readonly SolutionEvents _solutionEvents;
    private readonly BuildEvents    _buildEvents;
    private readonly WindowEvents   _windowEvents;

    // Item-level tracking (add/remove/rename of individual files) via the shell service —
    // DTE has no project-agnostic equivalent of these events.
    private readonly IVsTrackProjectDocuments2? _trackProjectDocuments;
    private uint _trackProjectDocumentsCookie;

    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();

    // Resolved lazily (not in the constructor) since MEF composition may not be ready yet at
    // construction time; cached thereafter. Used only for the MonitorOpenFeatureFile telemetry
    // signal below — every other responsibility in this class talks to DTE/the LSP pipe directly.
    private IVsIdeScope? _ideScope;

    /// <summary>
    /// Subscribes to DTE solution/build/window events and, if available,
    /// <see cref="IVsTrackProjectDocumentsEvents2"/>. Must be called on the UI thread.
    /// </summary>
    public VsProjectEventMonitor(
        LspInterceptingPipe pipe,
        ILogger<VsProjectEventMonitor> logger,
        IServiceProvider serviceProvider,
        DocumentActivationState activationState)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _pipe            = pipe            ?? throw new ArgumentNullException(nameof(pipe));
        _logger          = logger          ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _activationState = activationState ?? throw new ArgumentNullException(nameof(activationState));

        _dte = (DTE2)serviceProvider.GetService(typeof(DTE))
               ?? throw new InvalidOperationException("DTE service not available.");

        _solutionEvents = _dte.Events.SolutionEvents;
        _buildEvents    = _dte.Events.BuildEvents;
        _windowEvents   = _dte.Events.WindowEvents;

        _solutionEvents.ProjectAdded   += OnProjectAdded;
        _solutionEvents.ProjectRemoved += OnProjectRemoved;
        _solutionEvents.Opened         += OnSolutionOpened;
        _solutionEvents.AfterClosing   += OnSolutionClosed;
        _buildEvents.OnBuildDone       += OnBuildDone;
        _windowEvents.WindowActivated  += OnWindowActivated;

        _trackProjectDocuments = serviceProvider.GetService(typeof(SVsTrackProjectDocuments))
            as IVsTrackProjectDocuments2;
        if (_trackProjectDocuments is not null &&
            ErrorHandler.Succeeded(_trackProjectDocuments.AdviseTrackProjectDocumentsEvents(
                this, out _trackProjectDocumentsCookie)))
        {
            _logger.LogInformation("VsProjectEventMonitor: subscribed to IVsTrackProjectDocumentsEvents2.");
        }
        else
        {
            _logger.LogWarning(
                "VsProjectEventMonitor: could not subscribe to IVsTrackProjectDocumentsEvents2; " +
                "per-file add/remove/rename will not be reflected until the next build or solution reload.");
            _trackProjectDocuments = null;
        }
    }

    // ── Initial flush ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <c>reqnroll/projectLoaded</c> and <c>reqnroll/projectFiles</c> for every project
    /// currently in the solution.  Call once immediately after the server has initialised.
    /// </summary>
    public async Task SendInitialProjectsAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var solution = _dte.Solution;
        if (solution?.IsOpen != true)
            return;

        foreach (Project project in solution.Projects)
        {
            await TrySendProjectLoadedAsync(project, ct).ConfigureAwait(false);
            await TrySendProjectFilesAsync(project, ct).ConfigureAwait(false);
        }
    }

    // ── Scaffold notification ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a <c>reqnroll/projectFiles</c> delta that registers a newly scaffolded
    /// <c>.cs</c> file in the server's membership index so Roslyn discovery accepts it.
    /// Must be called BEFORE the server receives <c>textDocument/didOpen</c> for the file.
    /// </summary>
    public async Task SendScaffoldedFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var project = FindProjectContaining(filePath);
            if (project is null)
            {
                _logger.LogWarning(
                    "VsProjectEventMonitor: no project found for scaffolded file {FilePath}", filePath);
                return;
            }

            var tfm = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;
            var paramsObj = new
            {
                projectFile            = project.FullName,
                targetFrameworkMoniker = tfm,
                kind                   = 1,  // ProjectFilesKind.Delta = 1
                files                  = new[] { new { path = filePath, role = 1, added = true } }
            };
            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _logger.LogInformation(
                "VsProjectEventMonitor: sent projectFiles delta for scaffolded file {FileName} in {ProjectName}",
                Path.GetFileName(filePath), project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "VsProjectEventMonitor: SendScaffoldedFileAsync failed for {FilePath}", filePath);
        }
    }

    // ── Document activation notification (issue #85) ──────────────────────────

    /// <summary>
    /// Sends <c>reqnroll/documentActivated</c> for a <c>.feature</c> file the user just switched
    /// to, so the server recomputes and republishes diagnostics/semantic tokens for it
    /// independent of whatever originally left it stale (see the #85 design discussion / #78).
    /// </summary>
    public Task SendDocumentActivatedAsync(string filePath, CancellationToken ct)
    {
        var featureUri = new Uri(filePath).AbsoluteUri;
        var paramsObj = new { uri = featureUri };
        var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);

        _logger.LogInformation(
            "VsProjectEventMonitor: sending documentActivated for {FileName}", Path.GetFileName(filePath));

        return _pipe.SendNotificationToServerAsync("reqnroll/documentActivated", paramsJson, ct);
    }

    private Project? FindProjectContaining(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solution = _dte.Solution;
        if (solution?.IsOpen != true)
            return null;

        Project? best    = null;
        int      bestLen = 0;
        foreach (Project project in solution.Projects)
        {
            if (!IsSolutionProject(project))
                continue;
            var folder = Path.GetDirectoryName(project.FullName) ?? string.Empty;
            if (PathUtils.IsUnderFolder(filePath, folder) &&
                folder.Length > bestLen)
            {
                best    = project;
                bestLen = folder.Length;
            }
        }
        return best;
    }

    // ── DTE event handlers ────────────────────────────────────────────────────

    private void OnProjectAdded(Project project)
        => FireAndForget(async ct =>
        {
            await TrySendProjectLoadedAsync(project, ct).ConfigureAwait(false);
            await TrySendProjectFilesAsync(project, ct).ConfigureAwait(false);
        });

    private void OnProjectRemoved(Project project)
        => FireAndForget(ct => TrySendProjectUnloadedAsync(project, ct));

    private void OnSolutionOpened()
        => FireAndForget(ct => SendInitialProjectsAsync(ct));

    private void OnSolutionClosed()
    {
        // Nothing to do — projects are removed individually via OnProjectRemoved before this fires.
    }

    private void OnWindowActivated(Window gotFocus, Window lostFocus)
    {
        // DTE classic automation events fire synchronously on the UI thread (single-threaded
        // apartment) — unlike VS.Extensibility's async APIs elsewhere in this codebase, which
        // explicitly warn about background-thread invocation.
        ThreadHelper.ThrowIfNotOnUIThread();

        // Logged unconditionally, synchronously, before the UI-thread hop below — so the log
        // proves whether DTE ever raises this event at all for a given window, independent of
        // whatever the activation-state machine later decides to do with it (issue #85
        // debugging: a prior run showed zero reqnroll/documentActivated sends despite the tabs'
        // views clearly having been created, and there was no log evidence to say whether
        // WindowActivated simply never fired or fired and no-op'd).
        _logger.LogInformation(
            "VsProjectEventMonitor: WindowActivated fired, gotFocus.Caption={Caption}, document={DocumentPath}",
            TryGetCaption(gotFocus), TryGetDocumentPath(gotFocus));

        FireAndForget(ct => HandleWindowActivatedAsync(gotFocus, ct));
    }

    private async Task HandleWindowActivatedAsync(Window gotFocus, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var filePath = gotFocus.Document?.FullName;
        if (string.IsNullOrEmpty(filePath) ||
            !filePath!.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            return;

        var action = _activationState.OnWindowActivated(filePath);
        _logger.LogInformation(
            "VsProjectEventMonitor: WindowActivated for {FileName} — state action = {Action}",
            Path.GetFileName(filePath), action);

        if (action != DocumentActivationAction.SendNow)
            return;

        // Telemetry: fires exactly once per open-lifetime (SendNow only, guarded by the phase
        // machine above), matching "feature file opened" semantics rather than "activated" —
        // issue #255's gap analysis found MonitorOpenFeatureFile's implementation real but
        // uncalled anywhere; this is that signal's natural (and previously missing) trigger point.
        MonitorFeatureFileOpened(filePath);

        await SendDocumentActivatedAsync(filePath, ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort <see cref="ITelemetryService.MonitorOpenFeatureFile"/> call — never allowed to break document activation.</summary>
    private void MonitorFeatureFileOpened(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var project = FindProjectContaining(filePath);
            if (project is null)
                return;

            _ideScope ??= VsUtils.ResolveMefDependency<IVsIdeScope>(_serviceProvider);
            if (_ideScope is null)
                return;

            var projectScope = _ideScope.GetProjectScope(project);
            var settings = projectScope.GetProjectSettings();
            _ideScope.TelemetryService.MonitorOpenFeatureFile(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VsProjectEventMonitor: MonitorFeatureFileOpened failed for {FilePath}", filePath);
        }
    }

    // Best-effort, UI-thread-safe reads for the diagnostic log line above — must not throw or
    // block on COM calls that could be unsafe from whatever thread DTE invokes this event on.
    private static string TryGetCaption(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return window.Caption ?? "(null)"; }
        catch (Exception ex) { return $"(error: {ex.Message})"; }
    }

    private static string TryGetDocumentPath(Window window)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return window.Document?.FullName ?? "(no document)"; }
        catch (Exception ex) { return $"(error: {ex.Message})"; }
    }

    private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
    {
        // After any successful build re-send all projects so the server gets updated
        // OutputAssemblyPath values and a fresh file-membership baseline (build may have changed
        // conditional compilation/references that affect which files are included).
        if (action == vsBuildAction.vsBuildActionBuild ||
            action == vsBuildAction.vsBuildActionRebuildAll)
        {
            FireAndForget(ct => SendInitialProjectsAsync(ct));
        }
    }

    // ── Notification builders ─────────────────────────────────────────────────

    private async Task TrySendProjectLoadedAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsJson = VsProjectPayloadBuilder.BuildProjectLoadedParamsJson(
                project, GetSolutionFolder(), _serviceProvider, _logger);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectLoaded", paramsJson, ct)
                       .ConfigureAwait(false);

            _logger.LogInformation(
                "VsProjectEventMonitor: sent projectLoaded for {ProjectName}", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "VsProjectEventMonitor: failed to send projectLoaded for project.");
        }
    }

    private async Task TrySendProjectUnloadedAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsObj = new { projectFile = project.FullName };
            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);

            await _pipe.SendNotificationToServerAsync("reqnroll/projectUnloaded", paramsJson, ct)
                       .ConfigureAwait(false);

            _logger.LogInformation(
                "VsProjectEventMonitor: sent projectUnloaded for {ProjectName}", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "VsProjectEventMonitor: failed to send projectUnloaded.");
        }
    }

    private async Task TrySendProjectFilesAsync(Project project, CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!IsSolutionProject(project))
                return;

            var paramsJson = VsProjectPayloadBuilder.BuildProjectFilesParamsJson(project, _logger);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _logger.LogInformation(
                "VsProjectEventMonitor: sent projectFiles baseline for {ProjectName}", project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "VsProjectEventMonitor: failed to send projectFiles for {ProjectName}", project.Name);
        }
    }

    // ── IVsTrackProjectDocumentsEvents2 (per-file add/remove/rename) ─────────────

    /// <summary>Fires after files are renamed; sends a <c>reqnroll/projectFiles</c> delta reflecting the rename.</summary>
    public int OnAfterRenameFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices,
        string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEFILEFLAGS[] rgFlags)
    {
        FireAndForget(ct => HandleRenameFilesAsync(cProjects, cFiles, rgpProjects, rgFirstIndices,
            rgszMkOldNames, rgszMkNewNames, ct));
        return VSConstants.S_OK;
    }

    /// <summary>Fires after files are added; sends a <c>reqnroll/projectFiles</c> delta adding the tracked ones.</summary>
    public int OnAfterAddFilesEx(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices,
        string[] rgpszMkDocuments, VSADDFILEFLAGS[] rgFlags)
    {
        FireAndForget(ct => HandleAddOrRemoveFilesAsync(cProjects, cFiles, rgpProjects, rgFirstIndices,
            rgpszMkDocuments, added: true, ct));
        return VSConstants.S_OK;
    }

    /// <summary>Fires after files are removed; sends a <c>reqnroll/projectFiles</c> delta removing the tracked ones.</summary>
    public int OnAfterRemoveFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices,
        string[] rgpszMkDocuments, VSREMOVEFILEFLAGS[] rgFlags)
    {
        FireAndForget(ct => HandleAddOrRemoveFilesAsync(cProjects, cFiles, rgpProjects, rgFirstIndices,
            rgpszMkDocuments, added: false, ct));
        return VSConstants.S_OK;
    }

    private async Task HandleRenameFilesAsync(int projectCount, int fileCount, IVsProject[] projects,
        int[] fileStartIndices, string[] oldPaths, string[] newPaths, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        foreach (var (vsProject, start, count) in GroupByProject(projectCount, fileCount, projects, fileStartIndices))
        {
            var project = ResolveProject(vsProject);
            if (project is null)
                continue;

            var entries = new List<object>();
            for (var i = start; i < start + count; i++)
            {
                if (ClassifyRole(oldPaths[i]) is { } oldRole)
                    entries.Add(new { path = oldPaths[i], role = oldRole, added = false });
                if (ClassifyRole(newPaths[i]) is { } newRole)
                    entries.Add(new { path = newPaths[i], role = newRole, added = true });
            }

            if (entries.Count > 0)
                await SendProjectFilesDeltaAsync(project, entries, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAddOrRemoveFilesAsync(int projectCount, int fileCount, IVsProject[] projects,
        int[] fileStartIndices, string[] paths, bool added, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        foreach (var (vsProject, start, count) in GroupByProject(projectCount, fileCount, projects, fileStartIndices))
        {
            var project = ResolveProject(vsProject);
            if (project is null)
                continue;

            var entries = new List<object>();
            for (var i = start; i < start + count; i++)
            {
                if (ClassifyRole(paths[i]) is { } role)
                    entries.Add(new { path = paths[i], role, added });
            }

            if (entries.Count > 0)
                await SendProjectFilesDeltaAsync(project, entries, ct).ConfigureAwait(false);
        }
    }

    private Project? ResolveProject(IVsProject vsProject)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var project = VsUtils.GetProjectFromHierarchy(vsProject as IVsHierarchy);
        return project is not null && IsSolutionProject(project) ? project : null;
    }

    /// <summary>Splits the flat per-call file arrays back into (project, range) groups.</summary>
    internal static IEnumerable<(IVsProject Project, int Start, int Count)> GroupByProject(
        int projectCount, int fileCount, IVsProject[] projects, int[] fileStartIndices)
    {
        for (var i = 0; i < projectCount; i++)
        {
            var start = fileStartIndices[i];
            var end   = i + 1 < projectCount ? fileStartIndices[i + 1] : fileCount;
            if (end > start)
                yield return (projects[i], start, end - start);
        }
    }

    /// <summary>Classifies a file by extension the same way <see cref="VsProjectPayloadBuilder"/> does;
    /// returns <see langword="null"/> for files the membership index does not track.</summary>
    internal static int? ClassifyRole(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".feature", StringComparison.OrdinalIgnoreCase))
            return 0; // ProjectFileRole.Feature
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return 1; // ProjectFileRole.Binding
        return null;
    }

    private async Task SendProjectFilesDeltaAsync(Project project, List<object> entries, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        try
        {
            var tfm = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;
            var paramsObj = new
            {
                projectFile            = project.FullName,
                targetFrameworkMoniker = tfm,
                kind                   = 1, // ProjectFilesKind.Delta
                files                  = entries,
            };
            var paramsJson = JsonConvert.SerializeObject(paramsObj, Formatting.None);
            await _pipe.SendNotificationToServerAsync("reqnroll/projectFiles", paramsJson, ct)
                       .ConfigureAwait(false);

            _logger.LogInformation(
                "VsProjectEventMonitor: sent projectFiles delta ({EntryCount} entrie(s)) for {ProjectName}",
                entries.Count, project.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "VsProjectEventMonitor: failed to send projectFiles delta for {ProjectName}", project.Name);
        }
    }

    // Directory and SCC events are not part of the file-membership index — no-ops that
    // approve/ignore. Query* methods leave the result arrays untouched (no veto).

    /// <summary>No-op approval — file adds are not vetoed.</summary>
    public int OnQueryAddFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments,
        VSQUERYADDFILEFLAGS[] rgFlags, VSQUERYADDFILERESULTS[] pSummaryResult, VSQUERYADDFILERESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op approval — directory adds are not vetoed.</summary>
    public int OnQueryAddDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments,
        VSQUERYADDDIRECTORYFLAGS[] rgFlags, VSQUERYADDDIRECTORYRESULTS[] pSummaryResult,
        VSQUERYADDDIRECTORYRESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op — directory adds are not part of the file-membership index.</summary>
    public int OnAfterAddDirectoriesEx(int cProjects, int cDirectories, IVsProject[] rgpProjects,
        int[] rgFirstIndices, string[] rgpszMkDocuments, VSADDDIRECTORYFLAGS[] rgFlags)
        => VSConstants.S_OK;

    /// <summary>No-op approval — file removals are not vetoed.</summary>
    public int OnQueryRemoveFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments,
        VSQUERYREMOVEFILEFLAGS[] rgFlags, VSQUERYREMOVEFILERESULTS[] pSummaryResult,
        VSQUERYREMOVEFILERESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op approval — directory removals are not vetoed.</summary>
    public int OnQueryRemoveDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments,
        VSQUERYREMOVEDIRECTORYFLAGS[] rgFlags, VSQUERYREMOVEDIRECTORYRESULTS[] pSummaryResult,
        VSQUERYREMOVEDIRECTORYRESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op — directory removals are not part of the file-membership index.</summary>
    public int OnAfterRemoveDirectories(int cProjects, int cDirectories, IVsProject[] rgpProjects,
        int[] rgFirstIndices, string[] rgpszMkDocuments, VSREMOVEDIRECTORYFLAGS[] rgFlags)
        => VSConstants.S_OK;

    /// <summary>No-op approval — file renames are not vetoed.</summary>
    public int OnQueryRenameFiles(IVsProject pProject, int cFiles, string[] rgszMkOldNames,
        string[] rgszMkNewNames, VSQUERYRENAMEFILEFLAGS[] rgFlags, VSQUERYRENAMEFILERESULTS[] pSummaryResult,
        VSQUERYRENAMEFILERESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op approval — directory renames are not vetoed.</summary>
    public int OnQueryRenameDirectories(IVsProject pProject, int cDirs, string[] rgszMkOldNames,
        string[] rgszMkNewNames, VSQUERYRENAMEDIRECTORYFLAGS[] rgFlags,
        VSQUERYRENAMEDIRECTORYRESULTS[] pSummaryResult, VSQUERYRENAMEDIRECTORYRESULTS[] rgResults)
        => VSConstants.S_OK;

    /// <summary>No-op — directory renames are not part of the file-membership index.</summary>
    public int OnAfterRenameDirectories(int cProjects, int cDirs, IVsProject[] rgpProjects, int[] rgFirstIndices,
        string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEDIRECTORYFLAGS[] rgFlags)
        => VSConstants.S_OK;

    /// <summary>No-op — source-control status changes do not affect the file-membership index.</summary>
    public int OnAfterSccStatusChanged(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices,
        string[] rgpszMkDocuments, uint[] rgdwSccStatus)
        => VSConstants.S_OK;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetSolutionFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solutionFile = _dte.Solution?.FullName;
        return string.IsNullOrEmpty(solutionFile)
            ? string.Empty
            : Path.GetDirectoryName(solutionFile) ?? string.Empty;
    }

    private static bool IsSolutionProject(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsUtils.IsSolutionProject(project);
    }

    private void FireAndForget(Func<CancellationToken, Task> action)
    {
        var ct = _cts.Token;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VsProjectEventMonitor: background task failed.");
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Unsubscribes from all DTE and project-document events. Must be called on the UI thread.</summary>
    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        _solutionEvents.ProjectAdded   -= OnProjectAdded;
        _solutionEvents.ProjectRemoved -= OnProjectRemoved;
        _solutionEvents.Opened         -= OnSolutionOpened;
        _solutionEvents.AfterClosing   -= OnSolutionClosed;
        _buildEvents.OnBuildDone       -= OnBuildDone;
        _windowEvents.WindowActivated  -= OnWindowActivated;

        if (_trackProjectDocuments is not null && _trackProjectDocumentsCookie != 0)
            _trackProjectDocuments.UnadviseTrackProjectDocumentsEvents(_trackProjectDocumentsCookie);
    }
}
