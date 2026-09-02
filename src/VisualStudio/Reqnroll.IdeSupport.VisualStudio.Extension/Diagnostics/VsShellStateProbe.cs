#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;

/// <summary>
/// Reads the live shell and solution state into a <see cref="VsShellStateSnapshot"/> (issue #555).
/// </summary>
/// <remarks>
/// Every read is individually guarded: this exists to explain a failure, so it must produce a
/// usable answer even when half the shell is unreachable, and must never throw into the caller —
/// the callers are a cancellation callback and a timer, neither of which has anywhere to put an
/// exception.
/// </remarks>
internal static class VsShellStateProbe
{
    /// <summary>
    /// Switches to the UI thread and reads shell/solution state, giving up after
    /// <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The timeout is the point of the method, not a nicety. During a genuine IDE shutdown the UI
    /// thread stops pumping, so an unbounded <c>SwitchToMainThreadAsync</c> would hang forever and
    /// the heartbeat that calls this would silently stop — producing exactly the "log just ends"
    /// signature we are trying to tell apart from a real exit. Timing out and saying so keeps the
    /// two distinguishable.
    /// </remarks>
    public static async Task<VsShellStateSnapshot> CaptureAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
            return Capture();
        }
        catch (OperationCanceledException)
        {
            return VsShellStateSnapshot.Failed(
                $"UI thread did not respond within {timeout.TotalMilliseconds:F0}ms — consistent with a shutting-down or blocked shell");
        }
        catch (Exception ex)
        {
            return VsShellStateSnapshot.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Reads shell/solution state. Must be called on the UI thread.</summary>
    private static VsShellStateSnapshot Capture()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var shell    = ServiceProvider.GlobalProvider.GetService(typeof(SVsShell))    as IVsShell;
        var solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;

        return new VsShellStateSnapshot(
            ShellInitialized:     ReadShellBool(shell, (int)__VSSPROPID4.VSSPROPID_ShellInitialized),
            ShellShutdownStarted: ReadShellBool(shell, (int)__VSSPROPID6.VSSPROPID_ShutdownStarted),
            SolutionOpen:         ReadSolutionBool(solution, (int)__VSPROPID.VSPROPID_IsSolutionOpen),
            SolutionClosing:      ReadSolutionBool(solution, (int)__VSPROPID2.VSPROPID_IsSolutionClosing),
            SolutionFileName:     ReadSolutionString(solution, (int)__VSPROPID.VSPROPID_SolutionFileName),
            ProbeError:           shell is null && solution is null ? "neither SVsShell nor SVsSolution resolved" : null);
    }

    private static bool? ReadShellBool(IVsShell? shell, int propId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (shell is null) return null;

        try
        {
            return shell.GetProperty(propId, out var value) == Microsoft.VisualStudio.VSConstants.S_OK
                ? value as bool?
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadSolutionBool(IVsSolution? solution, int propId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (solution is null) return null;

        try
        {
            return solution.GetProperty(propId, out var value) == Microsoft.VisualStudio.VSConstants.S_OK
                ? value as bool?
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSolutionString(IVsSolution? solution, int propId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (solution is null) return null;

        try
        {
            return solution.GetProperty(propId, out var value) == Microsoft.VisualStudio.VSConstants.S_OK
                ? value as string
                : null;
        }
        catch
        {
            return null;
        }
    }
}
