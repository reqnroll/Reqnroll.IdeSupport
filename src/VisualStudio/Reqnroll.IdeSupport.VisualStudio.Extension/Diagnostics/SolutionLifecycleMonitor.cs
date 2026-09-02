#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;

/// <summary>
/// Logs solution open/close phases so a shutdown-token firing can be placed relative to them
/// (issue #555).
/// </summary>
/// <remarks>
/// <para>
/// The repro is "switch solutions with File &gt; Open Recent Solution and the LSP never comes
/// back". <c>DocumentInitializationMonitor</c> already shows individual documents closing and
/// opening around that moment, but a solution swap is two solution-level transactions with a
/// window in between, and document events cannot say which side of that window the token fired on.
/// These four lines can: if <c>VsShellUtilities.ShutdownToken</c> lands between
/// <c>OnBeforeCloseSolution</c> and <c>OnAfterOpenSolution</c>, it is a solution-close signal
/// wearing a shutdown-signal's name, and the fix belongs in what the extension listens to rather
/// than in how it recovers.
/// </para>
/// <para>
/// Project-level events are logged at Verbose: a solution swap fires them once per project, which
/// would drown the four lines that matter in a shipped-build log (the extension's file logger sits
/// at Info). Every <see cref="IVsSolutionEvents"/> callback arrives on the UI thread.
/// </para>
/// </remarks>
internal sealed class SolutionLifecycleMonitor : IVsSolutionEvents, IDisposable
{
    private readonly IVsSolution _solution;
    private readonly IIdeSupportLogger _logger;
    private readonly Stopwatch _sinceAdvise;
    private uint _cookie;
    private bool _disposed;

    private SolutionLifecycleMonitor(IVsSolution solution, IIdeSupportLogger logger)
    {
        _solution = solution;
        _logger = logger;
        _sinceAdvise = Stopwatch.StartNew();
    }

    /// <summary>
    /// Subscribes to solution events. Returns <see langword="null"/> if the solution service is
    /// unavailable or the subscription fails — diagnostics must never break package
    /// initialization.
    /// </summary>
    public static SolutionLifecycleMonitor? TryAdvise(IVsSolution? solution, IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (solution is null)
        {
            logger.LogInfo("SolutionLifecycleMonitor: IVsSolution unavailable; not advising.");
            return null;
        }

        var monitor = new SolutionLifecycleMonitor(solution, logger);
        try
        {
            solution.AdviseSolutionEvents(monitor, out monitor._cookie);
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "SolutionLifecycleMonitor: AdviseSolutionEvents failed.");
            return null;
        }

        logger.LogInfo("SolutionLifecycleMonitor: advised — solution open/close phases will be logged (issue #555).");
        return monitor;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Switches to the UI thread itself rather than asserting, so callers (the package's
    /// <c>Dispose(bool)</c>) need not already be on it — matching
    /// <see cref="DocumentInitializationMonitor.Dispose"/>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cookie == 0) return;

        try
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _solution.UnadviseSolutionEvents(_cookie);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "SolutionLifecycleMonitor: UnadviseSolutionEvents failed.");
        }
        finally
        {
            _cookie = 0;
        }
    }

    // ── Solution-level events (Info): the four that bracket a solution swap ────────────────────

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogSolutionEvent($"OnAfterOpenSolution (newSolution={fNewSolution != 0})");
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogSolutionEvent("OnQueryCloseSolution");
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogSolutionEvent("OnBeforeCloseSolution");
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogSolutionEvent("OnAfterCloseSolution");
        return VSConstants.S_OK;
    }

    // ── Project-level events (Verbose): one per project on every swap ─────────────────────────

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnAfterOpenProject", pHierarchy);
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnQueryCloseProject", pHierarchy);
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnBeforeCloseProject", pHierarchy);
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnAfterLoadProject", pRealHierarchy);
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnQueryUnloadProject", pRealHierarchy);
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LogProjectEvent("OnBeforeUnloadProject", pRealHierarchy);
        return VSConstants.S_OK;
    }

    private void LogSolutionEvent(string eventName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var solutionFile = "(unknown)";
        try
        {
            if (_solution.GetProperty((int)__VSPROPID.VSPROPID_SolutionFileName, out var value) == VSConstants.S_OK
                && value is string s && !string.IsNullOrEmpty(s))
            {
                solutionFile = s;
            }
        }
        catch
        {
            // Best-effort: the solution file name is unreadable mid-close, which is itself normal.
        }

        _logger.LogInfo(
            $"SolutionLifecycleMonitor: {eventName} at +{FormatElapsed()} after advise — solution={solutionFile}");
    }

    private void LogProjectEvent(string eventName, IVsHierarchy? hierarchy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_logger.IsLogging(TraceLevel.Verbose))
            return;

        var name = "(unknown)";
        try
        {
            if (hierarchy is not null
                && hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Name, out var value) == VSConstants.S_OK
                && value is string s)
            {
                name = s;
            }
        }
        catch
        {
            // Best-effort, as above.
        }

        _logger.LogVerbose($"SolutionLifecycleMonitor: {eventName} at +{FormatElapsed()} after advise — project={name}");
    }

    private string FormatElapsed() =>
        _sinceAdvise.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
}
