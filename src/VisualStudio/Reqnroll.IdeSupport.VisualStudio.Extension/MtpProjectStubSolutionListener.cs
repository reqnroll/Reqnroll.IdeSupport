using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using NuGet.VisualStudio;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Keeps every Reqnroll C# project of the open solution connected to the bundled Microsoft.Testing.Platform
/// reporter through its project-local <c>obj\&lt;Project&gt;.csproj.reqnroll-ide.targets</c> stub
/// (issue #741, <see cref="MtpProjectStubs.TrySyncStub"/>), and keeps other projects free of one: once
/// for the projects already loaded when the package initializes, then on every solution open (a solution
/// switch), every project load/add, and every NuGet restore — which is when a project's Reqnroll use
/// becomes known on a fresh clone, or changes when a Reqnroll package is added or removed. Syncs are
/// idempotent, so a project reported more than once costs a couple of file reads. File work runs off
/// the UI thread.
/// </summary>
internal sealed class MtpProjectStubSolutionListener : IVsSolutionEvents, IDisposable
{
    private readonly IVsSolution _solution;
    private readonly string _bundleTargetsPath;
    private readonly IIdeSupportLogger _logger;
    private IVsNuGetProjectUpdateEvents? _nugetProjectUpdateEvents;
    private uint _cookie;

    private MtpProjectStubSolutionListener(IVsSolution solution, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        _solution = solution;
        _bundleTargetsPath = bundleTargetsPath;
        _logger = logger;
    }

    /// <summary>Subscribes and immediately covers the already-open solution. Must be called on the UI thread; returns null (logged) on failure.</summary>
    public static MtpProjectStubSolutionListener? TryStart(IVsSolution solution, IServiceProvider serviceProvider, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var listener = new MtpProjectStubSolutionListener(solution, bundleTargetsPath, logger);
            ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(listener, out listener._cookie));
            listener.SubscribeToNuGetRestores(serviceProvider);
            listener.SyncStubsForOpenSolution();
            return listener;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpProjectStubSolutionListener)}: could not start.");
            return null;
        }
    }

    /// <summary>
    /// A project's Reqnroll use is read from its restore output, which a freshly cloned solution only
    /// gets once NuGet's restore finishes, and which changes when a Reqnroll package is added or removed.
    /// Without this signal such a project would get its stub only at the next solution open.
    /// </summary>
    private void SubscribeToNuGetRestores(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            _nugetProjectUpdateEvents = VsUtils.ResolveMefDependency<IVsNuGetProjectUpdateEvents>(serviceProvider);
        }
        catch (Exception ex)
        {
            // Not fatal: the solution-open and project-load passes still run.
            _logger.LogException(ex, $"{nameof(MtpProjectStubSolutionListener)}: could not resolve IVsNuGetProjectUpdateEvents.");
        }
        if (_nugetProjectUpdateEvents is null)
        {
            _logger.LogVerbose($"{nameof(MtpProjectStubSolutionListener)}: IVsNuGetProjectUpdateEvents not resolvable; a project restored after solution open gets its MTP reporter stub at the next solution open.");
            return;
        }
        _nugetProjectUpdateEvents.SolutionRestoreFinished += OnNuGetSolutionRestoreFinished;
    }

    /// <summary>Raised on a threadpool thread; hops to the UI thread only to list the solution's projects.</summary>
    private void OnNuGetSolutionRestoreFinished(IReadOnlyList<string> projects)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SyncStubsForOpenSolution();
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"{nameof(MtpProjectStubSolutionListener)}: syncing MTP reporter stubs after a NuGet restore failed.");
            }
        });
    }

    private void SyncStubsForOpenSolution()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var flags = (uint)__VSGETPROJFILESFLAGS.GPFF_SKIPUNLOADEDPROJECTS;
        if (ErrorHandler.Failed(_solution.GetProjectFilesInSolution(flags, 0, null, out var count)) || count == 0)
            return;

        var names = new string[count];
        if (ErrorHandler.Failed(_solution.GetProjectFilesInSolution(flags, count, names, out _)))
            return;

        Schedule(names.Where(n => !string.IsNullOrEmpty(n) && Path.IsPathRooted(n)).ToList());
    }

    private void Schedule(IReadOnlyList<string> projectFiles)
    {
        if (projectFiles.Count == 0) return;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await TaskScheduler.Default;
                var written = MtpProjectStubs.SyncStubs(projectFiles, _bundleTargetsPath, _logger);
                _logger.LogVerbose($"{nameof(MtpProjectStubSolutionListener)}: MTP reporter stubs in place for {written} Reqnroll project(s) of {projectFiles.Count}.");
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"{nameof(MtpProjectStubSolutionListener)}: writing MTP reporter stubs failed.");
            }
        });
    }

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        SyncStubsForOpenSolution();
        return VSConstants.S_OK;
    }

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (pHierarchy is not null
            && ErrorHandler.Succeeded(pHierarchy.GetCanonicalName((uint)VSConstants.VSITEMID.Root, out var projectFile))
            && !string.IsNullOrEmpty(projectFile)
            && Path.IsPathRooted(projectFile))
        {
            Schedule(new[] { projectFile });
        }
        return VSConstants.S_OK;
    }

    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_nugetProjectUpdateEvents is not null)
        {
            _nugetProjectUpdateEvents.SolutionRestoreFinished -= OnNuGetSolutionRestoreFinished;
            _nugetProjectUpdateEvents = null;
        }
        if (_cookie != 0)
        {
            _solution.UnadviseSolutionEvents(_cookie);
            _cookie = 0;
        }
    }
}
