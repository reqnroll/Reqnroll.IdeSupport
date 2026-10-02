#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Logging;

/// <summary>
/// Dedicated "Reqnroll" VS Output Window pane sink (issue #651) — the LSP-based extension's
/// counterpart to the legacy Reqnroll.VisualStudio <c>VsDeveroomOutputPaneServices</c>/
/// <c>OutputWindowPaneLogger</c> pair, which the new architecture never carried over. Previously
/// the "Reqnroll Language Client" pane visible in VS was VS.Extensibility's own generic LSP-trace
/// pane, and application-level messages (e.g. the "Rename failed" report that surfaced this) were
/// only ever written to the per-PID file log under %LOCALAPPDATA%\Reqnroll\logs\.
/// </summary>
/// <remarks>
/// Pane creation and writes are STA-bound, so <see cref="Log"/> posts the work to the UI thread
/// fire-and-forget (mirroring <c>VsProjectEventMonitor.FireAndForget</c>) rather than blocking
/// whichever thread produced the message. Like the legacy pane, a Warning-or-worse message
/// activates the pane and focuses the Output tool window so failures are visible without the user
/// going looking for them. Any failure to create or write to the pane (e.g. no VS host, as in
/// unit tests) is swallowed the same way <see cref="SynchronousFileLogger"/> swallows write
/// failures - a broken output pane must never take logging itself down.
/// <para>
/// Lives in VSSDKIntegration (not the Extension project) so the one shared instance in
/// <see cref="ExtensionHostLogger"/> can serve both composition roots - VS.Extensibility DI and
/// VSSDK MEF (issue #748). It must never be composed into the out-of-process CodeLens host's logger
/// (<see cref="CodeLensHostLogger"/>): that process has no VS shell to write a pane to.
/// </para>
/// </remarks>
internal sealed class VsOutputPaneLogger : IIdeSupportLogger
{
    private const string PaneName = "Reqnroll";
    private static readonly Guid PaneGuid = Guid.NewGuid();

    // Null means "use ServiceProvider.GlobalProvider", resolved lazily on the UI thread in
    // GetOrCreatePane/WriteToPane rather than here: the shared instance may now be constructed
    // from a MEF export on a background thread, where touching GlobalProvider is not safe.
    private readonly IServiceProvider? _serviceProvider;
    private IVsOutputWindowPane? _pane;
    private bool _paneCreationAttempted;
    private bool _logPointerShown;

    /// <summary>Gets the minimum trace level that will be written to the pane.</summary>
    public TraceLevel Level { get; }

    /// <summary>Initializes a new instance of the <see cref="VsOutputPaneLogger"/> class.</summary>
    public VsOutputPaneLogger(TraceLevel level = TraceLevel.Info, IServiceProvider? serviceProvider = null)
    {
        Level = level;
        _serviceProvider = serviceProvider;
    }

    private IServiceProvider ServiceProviderOnUIThread
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _serviceProvider ?? ServiceProvider.GlobalProvider;
        }
    }

    /// <summary>Posts the message to the "Reqnroll" output pane on the UI thread, if within <see cref="Level"/>.</summary>
    public void Log(LogMessage message)
    {
        if (message.Level > Level) return;

        var line = FormatLine(message, ConsumeLogPointerIfNeeded(message.Exception != null));
        var activate = ShouldActivate(message.Level);

        // Deliberately fire-and-forget: a log call must never block on the UI thread, and the body
        // catches everything, so there is no fault to observe. (The Extension project, where this
        // class lived before issue #748, suppresses VSSDK007 project-wide; VSSDKIntegration doesn't.)
#pragma warning disable VSSDK007
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
#pragma warning restore VSSDK007
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

    /// <summary>
    /// Formats <paramref name="message"/> for the pane: a one-line summary, not the full trace
    /// (issue #680) -- <see cref="SynchronousFileLogger"/> already writes the full exception,
    /// indented, to the per-process debug log at <see cref="Level"/>-or-Warning, so nothing is
    /// lost by trimming what the pane shows. <paramref name="logPointer"/>, when non-null, is
    /// appended as a trailing line pointing the user at that file; pass it only for the first
    /// exception-carrying message in a session (see <see cref="ConsumeLogPointerIfNeeded"/>) so
    /// it doesn't repeat on every subsequent one.
    /// </summary>
    internal static string FormatLine(LogMessage message, string? logPointer = null)
    {
        var line = $"{LogLineFormatter.FormatPreamble(message)}: {message.Message}";
        if (message.Exception != null)
        {
            var (typeName, exceptionMessage) = UnwrapForSummary(message.Exception);
            line += $" ({typeName}: {exceptionMessage})";
            if (logPointer != null)
                line += $"{Environment.NewLine}Details are in {logPointer}";
        }
        return line;
    }

    /// <summary>
    /// Unwraps the exception-chain wrapper types that would otherwise dominate the summary with an
    /// unhelpful "AggregateException: One or more errors occurred" (etc.) instead of the actual
    /// failure, mirroring how a developer reading the full trace would skip straight to the inner
    /// exception. Only unwraps <see cref="AggregateException"/> when it carries exactly one inner
    /// exception -- with more than one, "which one?" is itself the useful summary, so the wrapper's
    /// own message is kept.
    /// </summary>
    private static (string TypeName, string Message) UnwrapForSummary(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    exception = aggregate.InnerExceptions[0];
                    continue;
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    continue;
                case TypeInitializationException { InnerException: { } inner }:
                    exception = inner;
                    continue;
                default:
                    return (exception.GetType().Name, exception.Message);
            }
        }
    }

    /// <summary>
    /// Returns the file-log pointer text the first time an exception-carrying message is logged in
    /// this pane's lifetime, and <see langword="null"/> every time after (issue #680) -- repeating
    /// it on every message would itself become the noise this issue is trying to remove.
    /// </summary>
    internal string? ConsumeLogPointerIfNeeded(bool messageHasException)
    {
        if (!messageHasException || _logPointerShown) return null;
        _logPointerShown = true;
        return ReqnrollLogPaths.ResolveLogDirectory();
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
        if (ServiceProviderOnUIThread.GetService(typeof(DTE)) is DTE2 dte)
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

        if (ServiceProviderOnUIThread.GetService(typeof(SVsOutputWindow)) is not IVsOutputWindow outputWindow)
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
