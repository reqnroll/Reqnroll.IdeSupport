#nullable enable

using System;
using System.Diagnostics;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Logging;

/// <summary>
/// Dedicated "Reqnroll" VS Output Window pane sink (issue #651) — the LSP-based extension's
/// counterpart to the legacy Reqnroll.VisualStudio <c>VsDeveroomOutputPaneServices</c>/
/// <c>OutputWindowPaneLogger</c> pair, which the new architecture never carried over. Previously
/// the "Reqnroll Language Client" pane visible in VS was VS.Extensibility's own generic LSP-trace
/// pane, and application-level messages (e.g. the "Rename failed" report that surfaced this) were
/// only ever written to the per-PID file log under %LOCALAPPDATA%\Reqnroll\.
/// </summary>
/// <remarks>
/// Pane creation and writes are STA-bound, so <see cref="Log"/> posts the work to the UI thread
/// fire-and-forget (mirroring <c>VsProjectEventMonitor.FireAndForget</c>) rather than blocking
/// whichever thread produced the message. Like the legacy pane, a Warning-or-worse message
/// activates the pane and focuses the Output tool window so failures are visible without the user
/// going looking for them. Any failure to create or write to the pane (e.g. no VS host, as in
/// unit tests) is swallowed the same way <see cref="SynchronousFileLogger"/> swallows write
/// failures - a broken output pane must never take logging itself down.
/// </remarks>
internal sealed class VsOutputPaneLogger : IIdeSupportLogger
{
    private const string PaneName = "Reqnroll";
    private static readonly Guid PaneGuid = Guid.NewGuid();

    private readonly IServiceProvider _serviceProvider;
    private IVsOutputWindowPane? _pane;
    private bool _paneCreationAttempted;

    /// <summary>Gets the minimum trace level that will be written to the pane.</summary>
    public TraceLevel Level { get; }

    /// <summary>Initializes a new instance of the <see cref="VsOutputPaneLogger"/> class.</summary>
    public VsOutputPaneLogger(TraceLevel level = TraceLevel.Info, IServiceProvider? serviceProvider = null)
    {
        Level = level;
        _serviceProvider = serviceProvider ?? ServiceProvider.GlobalProvider;
    }

    /// <summary>Posts the message to the "Reqnroll" output pane on the UI thread, if within <see cref="Level"/>.</summary>
    public void Log(LogMessage message)
    {
        if (message.Level > Level) return;

        var line = FormatLine(message);
        var activate = ShouldActivate(message.Level);

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                WriteToPane(line, activate);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex, "Reqnroll output pane write error");
            }
        });
    }

    /// <summary>Formats <paramref name="message"/> the same way every other <see cref="IIdeSupportLogger"/> sink does.</summary>
    internal static string FormatLine(LogMessage message)
    {
        var line = $"{LogLineFormatter.FormatPreamble(message)}: {message.Message}";
        if (message.Exception != null)
            line += $"{Environment.NewLine}{message.Exception}";
        return line;
    }

    /// <summary>Matches the legacy pane's behavior: auto-activate on Warning-or-worse.</summary>
    internal static bool ShouldActivate(TraceLevel level) => level <= TraceLevel.Warning;

    private void WriteToPane(string line, bool activate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var pane = GetOrCreatePane();
        if (pane == null) return;

        pane.OutputStringThreadSafe(line + Environment.NewLine);
        if (!activate) return;

        pane.Activate();
        if (_serviceProvider.GetService(typeof(DTE)) is DTE2 dte)
            dte.ToolWindows.OutputWindow.Parent.Activate();
    }

    private IVsOutputWindowPane? GetOrCreatePane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane != null) return _pane;
        // Only ever try once - a service provider that can't produce SVsOutputWindow now
        // (e.g. no VS host) won't produce it later either, and retrying on every log message
        // would mean every failed attempt pays the same cost for nothing.
        if (_paneCreationAttempted) return null;
        _paneCreationAttempted = true;

        if (_serviceProvider.GetService(typeof(SVsOutputWindow)) is not IVsOutputWindow outputWindow)
            return null;

        var guid = PaneGuid;
        if (ErrorHandler.Failed(outputWindow.CreatePane(ref guid, PaneName, 1, 1)))
            return null;
        if (ErrorHandler.Failed(outputWindow.GetPane(ref guid, out var pane)))
            return null;

        _pane = pane;
        return _pane;
    }
}
