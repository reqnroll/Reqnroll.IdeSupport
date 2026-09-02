#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;

/// <summary>
/// Forensics for issue #555: establishes whether <c>VsShellUtilities.ShutdownToken</c> firing
/// really means "Visual Studio is exiting", and records what the shell was doing when it fired.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this class exists to answer.</b> <c>ExtensionEntrypoint</c> disposes the
/// process-wide <c>LspServerConnectionService</c> — killing the LSP server — when that token
/// fires, on the documented assumption that it fires once, at IDE shutdown. Issue #555 reports
/// that after switching solutions with <em>File &gt; Open Recent Solution</em>, no LSP service
/// ever works again for the rest of the session, and the extension log ends at exactly the
/// ShutdownToken line. Two readings fit that evidence equally well and imply opposite fixes:
/// </para>
/// <list type="number">
/// <item>The IDE really did exit (the log ends because the process ended), and the dead session
/// the user saw afterwards belongs to a <em>different</em>, later devenv process.</item>
/// <item>The IDE is alive and only the solution closed, so the token fired spuriously, the server
/// was killed, and nothing ever revives it — the log ends because the extension is neutered, not
/// because the process is gone.</item>
/// </list>
/// <para>
/// <b>How it tells them apart.</b> The post-token heartbeat below keeps writing a line every few
/// seconds after the token fires. Reading (1) produces no heartbeat lines at all — a dead process
/// writes nothing. Reading (2) produces a run of them. This is deliberately a positive signal for
/// the interesting case rather than an inference from silence, because silence is exactly what the
/// existing logs already give us and it is what makes the current evidence ambiguous.
/// </para>
/// <para>
/// Instrumentation only: nothing here changes disposal behaviour, and every path swallows its own
/// exceptions. A diagnostic that can break the extension it is diagnosing is worse than no
/// diagnostic.
/// </para>
/// </remarks>
internal sealed class LspLifecycleForensics
{
    private readonly IIdeSupportLogger _logger;
    private readonly Stopwatch _sinceLoad = Stopwatch.StartNew();

    private Timer? _heartbeatTimer;
    private int _heartbeatTicks;
    private int _heartbeatStarted;

    /// <summary>How often the post-token heartbeat writes a line.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many heartbeat ticks to write before stopping. Three minutes is well past the point
    /// where a real shutdown would have killed the process, and short enough that a session left
    /// open for hours does not accumulate noise.
    /// </summary>
    private const int MaxHeartbeatTicks = 36;

    /// <summary>How long to wait for the UI thread when probing shell state; see <see cref="VsShellStateProbe"/>.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Initializes a new instance of the <see cref="LspLifecycleForensics"/> class.</summary>
    public LspLifecycleForensics(IIdeSupportLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records which OS process this extension instance is running in, and hooks process-exit
    /// notifications.
    /// </summary>
    /// <remarks>
    /// The log file name already carries the PID, but only as a bare number, which is what made
    /// the first pass at issue #555 misread four sequential single-process logs as one session
    /// restarting its connection four times. Logging the process identity explicitly — name, id,
    /// start time — makes "same devenv or a different one?" answerable from the log's own contents
    /// rather than from filename archaeology.
    /// </remarks>
    public void LogProcessIdentity(string extensionAssemblyLocation)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            _logger.LogInfo(
                $"LspLifecycleForensics: host process {process.ProcessName} (PID {process.Id}), " +
                $"started {process.StartTime:yyyy-MM-ddTHH:mm:ss.fff}, " +
                $"uptime at extension load {FormatDuration(DateTime.Now - process.StartTime)}. " +
                $"Extension assembly: {extensionAssemblyLocation}. " +
                "RequiresInProcessHosting=true, so this is devenv.exe itself — one log file per IDE process.");
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "LspLifecycleForensics: could not read host process identity.");
        }

        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                SafeLog($"LspLifecycleForensics: AppDomain.ProcessExit at +{FormatDuration(_sinceLoad.Elapsed)} after extension load — the IDE process really is ending.");
            AppDomain.CurrentDomain.DomainUnload += (_, _) =>
                SafeLog($"LspLifecycleForensics: AppDomain.DomainUnload at +{FormatDuration(_sinceLoad.Elapsed)} after extension load.");
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "LspLifecycleForensics: could not hook AppDomain exit events.");
        }
    }

    /// <summary>
    /// Records a shutdown-token firing and starts the post-token heartbeat.
    /// </summary>
    /// <param name="tokenSource">
    /// Which token fired — <c>VsShellUtilities.ShutdownToken</c> or <c>ExtensionCore.ShutdownToken</c>.
    /// </param>
    /// <remarks>
    /// The stack trace is captured even though a cancellation callback's stack is usually just the
    /// CTS plumbing: on the chance that the cancellation is raised inline by whatever closed the
    /// solution, the frames above the callback are the shortest possible path to naming the
    /// culprit, and capturing one costs nothing on a path that runs once per process.
    /// </remarks>
    public void OnShutdownTokenFired(string tokenSource)
    {
        try
        {
            _logger.LogInfo(
                $"LspLifecycleForensics: {tokenSource} fired at +{FormatDuration(_sinceLoad.Elapsed)} after extension load, " +
                $"on thread {Environment.CurrentManagedThreadId} (UI thread: {ThreadHelper.CheckAccess()}).");
            _logger.LogVerbose(
                $"LspLifecycleForensics: {tokenSource} callback stack:{Environment.NewLine}{new StackTrace(fNeedFileInfo: true)}");
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "LspLifecycleForensics: could not record the shutdown-token firing.");
        }

        ThreadHelper.JoinableTaskFactory
            .RunAsync(async () =>
            {
                var state = await VsShellStateProbe.CaptureAsync(ProbeTimeout);
                SafeLog($"LspLifecycleForensics: shell state at {tokenSource}: {state.Describe()}");

                if (state.ContradictsShutdown)
                {
                    SafeLogWarning(
                        $"LspLifecycleForensics: {tokenSource} fired while the shell reports it is NOT shutting down " +
                        "(shellShutdownStarted=False). This is issue #555's signature: the LSP server is being disposed " +
                        "for a live session that has no path to restart it. Heartbeat lines below confirm whether the " +
                        "process survives.");
                }

                StartPostShutdownHeartbeat(tokenSource);
            })
            .FileAndForget("reqnroll/issue-555-shutdown-forensics");
    }

    /// <summary>
    /// Starts the post-token heartbeat: the positive signal that separates "process exited" from
    /// "process alive, extension neutered". Idempotent — both token registrations call it, and
    /// only the first firing starts a timer.
    /// </summary>
    private void StartPostShutdownHeartbeat(string tokenSource)
    {
        if (Interlocked.Exchange(ref _heartbeatStarted, 1) != 0)
            return;

        try
        {
            var sinceToken = Stopwatch.StartNew();
            _heartbeatTimer = new Timer(
                _ => HeartbeatTick(tokenSource, sinceToken),
                state: null,
                dueTime: HeartbeatInterval,
                period: HeartbeatInterval);

            _logger.LogInfo(
                $"LspLifecycleForensics: post-{tokenSource} heartbeat started — a line every " +
                $"{HeartbeatInterval.TotalSeconds:F0}s for up to {MaxHeartbeatTicks * HeartbeatInterval.TotalSeconds / 60:F0} minutes. " +
                "Any heartbeat line after this point proves the IDE process did not exit.");
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "LspLifecycleForensics: could not start the post-shutdown heartbeat.");
        }
    }

    private void HeartbeatTick(string tokenSource, Stopwatch sinceToken)
    {
        var tick = Interlocked.Increment(ref _heartbeatTicks);
        if (tick > MaxHeartbeatTicks)
        {
            StopHeartbeat();
            return;
        }

        // Logged before the shell probe, and without needing the UI thread, so the "still alive"
        // evidence survives even if the probe below times out against a blocked shell.
        SafeLog(
            $"LspLifecycleForensics: post-{tokenSource} heartbeat {tick}/{MaxHeartbeatTicks} " +
            $"at +{FormatDuration(sinceToken.Elapsed)} after the token fired — IDE process still alive.");

        ThreadHelper.JoinableTaskFactory
            .RunAsync(async () =>
            {
                var state = await VsShellStateProbe.CaptureAsync(ProbeTimeout);
                SafeLog($"LspLifecycleForensics: heartbeat {tick} shell state: {state.Describe()}");
            })
            .FileAndForget("reqnroll/issue-555-shutdown-heartbeat");

        if (tick == MaxHeartbeatTicks)
            StopHeartbeat();
    }

    private void StopHeartbeat()
    {
        try
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "LspLifecycleForensics: could not stop the heartbeat timer.");
        }
    }

    private void SafeLog(string message)
    {
        try { _logger.LogInfo(message); } catch { /* diagnostics must never throw into a timer or a CT callback */ }
    }

    private void SafeLogWarning(string message)
    {
        try { _logger.LogWarning(message); } catch { /* as above */ }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalSeconds < 60
            ? value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
            : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
