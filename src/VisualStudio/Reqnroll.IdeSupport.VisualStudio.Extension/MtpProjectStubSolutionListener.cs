using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Keeps every C# project of the open solution connected to the bundled Microsoft.Testing.Platform
/// reporter by writing its project-local <c>obj\&lt;Project&gt;.csproj.reqnroll-ide.targets</c> stub
/// (issue #741, <see cref="MtpProjectStubs"/>): once for the projects already loaded when the package
/// initializes, then on every solution open (a solution switch) and every project load/add. Stub writes
/// are idempotent, so a project reported more than once costs one file comparison. File work runs off
/// the UI thread.
/// </summary>
internal sealed class MtpProjectStubSolutionListener : IVsSolutionEvents, IDisposable
{
    private readonly IVsSolution _solution;
    private readonly string _bundleTargetsPath;
    private readonly IIdeSupportLogger _logger;
    private uint _cookie;

    private MtpProjectStubSolutionListener(IVsSolution solution, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        _solution = solution;
        _bundleTargetsPath = bundleTargetsPath;
        _logger = logger;
    }

    /// <summary>Subscribes and immediately covers the already-open solution. Must be called on the UI thread; returns null (logged) on failure.</summary>
    public static MtpProjectStubSolutionListener? TryStart(IVsSolution solution, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var listener = new MtpProjectStubSolutionListener(solution, bundleTargetsPath, logger);
            ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(listener, out listener._cookie));
            listener.WriteStubsForOpenSolution();
            return listener;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpProjectStubSolutionListener)}: could not start.");
            return null;
        }
    }

    private void WriteStubsForOpenSolution()
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
                var written = MtpProjectStubs.WriteStubs(projectFiles, _bundleTargetsPath, _logger);
                _logger.LogVerbose($"{nameof(MtpProjectStubSolutionListener)}: MTP reporter stubs in place for {written} of {projectFiles.Count} project(s).");
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
        WriteStubsForOpenSolution();
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
        if (_cookie != 0)
        {
            _solution.UnadviseSolutionEvents(_cookie);
            _cookie = 0;
        }
    }
}
