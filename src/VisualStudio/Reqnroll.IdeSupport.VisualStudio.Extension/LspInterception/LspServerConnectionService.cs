using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Nerdbank.Streams;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Owns the lifetime of the out-of-proc Reqnroll LSP server process and the
/// <see cref="LspInterceptingPipe"/> that sits between it and VS.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a DI singleton (see <c>ExtensionEntrypoint.InitializeServices</c>) and
/// constructor-injected into <see cref="ReqnrollLanguageClient"/>. VS.Extensibility constructs
/// <c>ReqnrollLanguageClient</c> when the extension loads — to read its
/// <c>LanguageServerProviderConfiguration</c> document filter — well before any <c>.feature</c>
/// file is opened. Injecting this service there is enough to trigger process launch and pipe
/// construction immediately, off the document-open path: this class's constructor starts the
/// work eagerly and caches the resulting task, so
/// <see cref="ReqnrollLanguageClient.CreateServerConnectionAsync"/> (invoked later, on first
/// matching document) just awaits an already-in-flight or already-completed task via
/// <see cref="GetConnectionAsync"/> instead of paying launch latency on that path.
/// </para>
/// <para>
/// <b>Issue #156:</b> <see cref="GetConnectionAsync"/> used to hand out the exact same
/// <see cref="IDuplexPipe"/> on every call — fine as long as VS only ever calls
/// <c>CreateServerConnectionAsync</c> once, but VS.Extensibility can and does call it again mid
/// session (confirmed by decompiling VS's own LSP client host: it's by-design extension hot-reload
/// support, not a VS bug). A second caller getting the same, already-in-use pipe corrupted the
/// connection outright — <c>System.InvalidOperationException: Writing is not allowed after writer
/// was completed</c> — the moment that happened. <see cref="GetConnectionAsync"/> now still awaits
/// the same eagerly-started server process/pipe construction (only ever done once), but calls
/// <see cref="LspInterceptingPipe.CreateFreshVsFacingPipe"/> fresh on every invocation, matching
/// every Microsoft sample's <c>CreateServerConnectionAsync</c> implementation (each builds a new
/// local duplex pipe pair per call rather than caching one). See that method's remarks for how the
/// local relay pipe is swapped without disturbing the real, persistent server process connection.
/// </para>
/// </remarks>
internal sealed class LspServerConnectionService : IDisposable
{
    private readonly ILogger<LspServerConnectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly StepCodeLensState _stepCodeLensState;
    private readonly DocumentActivationState _activationState = new();

    // JoinableTask (not a plain Task) so GetConnectionAsync's await is JTF-aware — avoids the
    // VSTHRD003 "awaiting a foreign task" analyzer error for a task started outside the awaiting
    // method's own async context. StartAsync itself never touches the UI thread.
    // Result is just success/failure now (issue #156): the actual IDuplexPipe handed to VS is no
    // longer produced once and cached here -- see GetConnectionAsync/CreateFreshVsFacingPipe.
    private readonly Microsoft.VisualStudio.Threading.JoinableTask<bool> _startTask;

    // Issue #555: distinguishes "the one and only instance in this process" from a hypothetical
    // rebuilt one. The first pass at that issue read four sequential single-instance logs as one
    // session rebuilding its connection four times; a number in the log makes the difference
    // self-evident instead of inferred, and shows in CreateServerConnectionAsync whether VS is
    // reconnecting to the same instance or a new one.
    private static int _instanceCounter;
    private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);

    private Process? _serverProcess;
    private LspInspectorLogger? _inspectorLogger;
    private LspInterceptingPipe? _interceptingPipe;
    private ChildProcessJob? _childJob;
    private ShutdownHandshakeInterceptor? _shutdownHandshakeInterceptor;
    private CodeLensRefreshInterceptor? _codeLensRefreshInterceptor;
    private bool _disposed;

    // How long to wait for the server to self-terminate after a graceful `exit` before falling
    // back to Kill(). See Dispose()/ShutdownServerAsync.
    private const int GracefulExitTimeoutMs = 3000;

    // How long to wait for a response to a `shutdown` request we send ourselves, when VS's own
    // client hasn't sent one by the time Dispose() runs. Confirmed empirically that VS's async
    // LSP-client-stop sequence does not reliably send `shutdown`
    // during VsShellUtilities.ShutdownToken-triggered teardown — a full 1000ms passive wait for it
    // never once observed one — so rather than wait on a request that may never arrive, we send it
    // ourselves on the still-live pipe via LspInterceptingPipe.SendRequestToServerAsync.
    private const int ShutdownRequestTimeoutMs = 2000;

    /// <summary>
    /// Creates the service and immediately kicks off server-process startup on a background
    /// JoinableTask; see the type-level remarks for why this happens eagerly in the constructor.
    /// </summary>
    public LspServerConnectionService(
        ILogger<LspServerConnectionService> logger, ILoggerFactory loggerFactory, StepCodeLensState stepCodeLensState)
    {
        _logger            = logger            ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory     = loggerFactory     ?? throw new ArgumentNullException(nameof(loggerFactory));
        _stepCodeLensState = stepCodeLensState ?? throw new ArgumentNullException(nameof(stepCodeLensState));

        _logger.LogInformation(
            "LspServerConnectionService: instance #{InstanceId} created — starting server eagerly.", _instanceId);

        // Fire off immediately; not awaited here. Consumers (ReqnrollLanguageClient) await
        // GetConnectionAsync() whenever they're ready, which may be well after this completes.
        _startTask = ThreadHelper.JoinableTaskFactory.RunAsync(StartAsync);
    }

    /// <summary>
    /// This instance's ordinal within the current IDE process, starting at 1 (issue #555). A value
    /// above 1 means the singleton really was rebuilt within one process — which the logs have
    /// never actually shown, and which the current disposal path provides no way to reach.
    /// </summary>
    public int InstanceId => _instanceId;

    /// <summary>
    /// The intercepting pipe once started; <c>null</c> until the server process and pipe have
    /// been constructed. Used by components (e.g. <see cref="VsProjectEventMonitor"/>) that need
    /// to send notifications directly to the server, bypassing VS.
    /// </summary>
    public LspInterceptingPipe? InterceptingPipe => _interceptingPipe;

    /// <summary>
    /// Set by <see cref="ReqnrollLanguageClient"/> once the MEF-resolved analytics transmitter is
    /// available (post-init, main thread). Read lazily by <see cref="TelemetryEventInterceptor"/>,
    /// which is constructed before this is known.
    /// </summary>
    public ITelemetryTransmitter? TelemetryTransmitter { get; set; }

    /// <summary>
    /// Set by <see cref="ReqnrollLanguageClient"/> once the project monitor is constructed
    /// (post-init, main thread — requires DTE). Read lazily by
    /// <see cref="ScaffoldTrackingInterceptor"/>, which is constructed before this is known.
    /// </summary>
    public VsProjectEventMonitor? ProjectMonitor { get; set; }

    /// <summary>
    /// Shared with <see cref="VsProjectEventMonitor"/> (constructed later, post-init) so both the
    /// send-pump-driven <see cref="DocumentActivationTrackingInterceptor"/> and the UI-thread
    /// <c>WindowActivated</c> listener observe/update the same per-file activation state
    /// (issue #85). Owned here rather than by either consumer since it must outlive and be
    /// constructed before both.
    /// </summary>
    public DocumentActivationState ActivationState => _activationState;

    /// <summary>
    /// Awaits the (already-started) server process and pipe construction, then hands back a fresh
    /// VS-facing <see cref="IDuplexPipe"/> for this call (issue #156 — see type remarks).
    /// </summary>
    /// <returns>
    /// A new <see cref="IDuplexPipe"/> each call, or <c>null</c> if server startup failed.
    /// </returns>
    public async Task<IDuplexPipe?> GetConnectionAsync()
    {
        // Issue #555: the predicted signature of a spurious ShutdownToken firing is VS asking for
        // a connection after this singleton has already been disposed — nothing re-resolves it, so
        // every such request returns null and the session stays dark. Logged as a warning because
        // the alternative reading of that issue (the IDE really was exiting) produces no such
        // request at all: a dead process does not reconnect.
        if (_disposed)
        {
            _logger.LogWarning(
                "LspServerConnectionService: instance #{InstanceId} was asked for a connection after disposal — " +
                "returning null. VS is still running and wants LSP service, but this singleton's server is gone and " +
                "nothing recreates it (issue #555).", _instanceId);
            return null;
        }

        var started = await _startTask.JoinAsync().ConfigureAwait(false);
        if (!started || _interceptingPipe is null)
            return null;

        return _interceptingPipe.CreateFreshVsFacingPipe();
    }

    /// <summary>
    /// Resolves the bundled LSP server executable path relative to the extension assembly's own
    /// location. Pure/deterministic — extracted so the path-building logic is unit-testable
    /// without touching <see cref="Process"/> or <see cref="ThreadHelper"/>.
    /// </summary>
    internal static string ResolveServerExePath(string extensionAssemblyLocation)
        => Path.Combine(
            Path.GetDirectoryName(extensionAssemblyLocation)!,
            "LSPServer",
            "Reqnroll.IdeSupport.LSP.Server.exe");

    /// <summary>
    /// The command-line arguments passed to the LSP server process: <c>--ide</c> selects the
    /// semantic token profile; <c>--log-level</c>, <c>--protocol-log-level</c>, and <c>--trace</c>
    /// set the server's own file logging, OmniSharp's internal diagnostics, and the LSP
    /// <c>$/logTrace</c> level respectively, rather than letting the server fall back to its own
    /// defaults independently. A DEBUG build of this extension (a developer F5-ing the extension
    /// project, not an installed VSIX) asks for the chattiest reasonable defaults across all three;
    /// a RELEASE build — what real users run — asks for quiet ones, since VS itself has no UI for
    /// a user to raise these afterward (unlike VS Code's <c>reqnroll.trace.server</c> setting).
    /// Extracted as a constant so it's unit-testable without spawning a process.
    /// </summary>
    internal const string ServerArguments =
#if DEBUG
        "--ide visualstudio --log-level Verbose --protocol-log-level Info --trace Verbose";
#else
        "--ide visualstudio --log-level Warning --protocol-log-level Warning --trace Off";
#endif

    private async Task<bool> StartAsync()
    {
        var serverExe = ResolveServerExePath(typeof(LspServerConnectionService).Assembly.Location);

        _logger.LogInformation("LspServerConnectionService: starting server. Server exe path: {ServerExe}", serverExe);

        if (!File.Exists(serverExe))
        {
            _logger.LogError("LspServerConnectionService: server executable not found at {ServerExe}.", serverExe);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(serverExe)
            {
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                Arguments              = ServerArguments,
            };

            _serverProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            // Fire-and-forget: pushes project/discovery data to the server's preload side
            // channel as soon as the solution is loaded, well before VS's own initialize
            // handshake (and hence CreateServerConnectionAsync) may happen. Must not be awaited
            // here — it can take up to ~60s (waiting for solution load) and must not delay
            // returning the pipe to VS. See LspProjectPreloadPusher's remarks.
            _ = LspProjectPreloadPusher.PushAsync(_serverProcess.Id, _logger, CancellationToken.None);

            // Assign to a kill-on-close Job Object so the server is terminated by the OS
            // when this VS process exits, even if Dispose is never called.
            try
            {
                _childJob = new ChildProcessJob();
                _childJob.AddProcess(_serverProcess);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LspServerConnectionService: could not assign server to Job Object.");
            }

            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    _logger.LogWarning("LSPServer stderr: {StdErr}", e.Data);
            };
            _serverProcess.BeginErrorReadLine();

            _logger.LogInformation("LspServerConnectionService: server process started (PID {ProcessId}).", _serverProcess.Id);

            IDuplexPipe rawPipe = new DuplexPipe(
                _serverProcess.StandardOutput.BaseStream.UsePipeReader(),
                _serverProcess.StandardInput.BaseStream.UsePipeWriter());

            // Build the LSP Inspector log file path, unique per session.
            var logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reqnroll");
            var logFile = Path.Combine(logDir, $"reqnroll-vs-inspector-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logger.LogInformation(
                "LspServerConnectionService: server process started (PID {ProcessId}). Inspector log: {LogFile}",
                _serverProcess.Id, logFile);
            _inspectorLogger = new LspInspectorLogger(logFile, _loggerFactory.CreateLogger<LspInspectorLogger>());

            // Observes semanticTokens traffic (both directions) and caches the decoded tokens so the
            // editor classifier can colour .feature files with Reqnroll's custom classifications,
            // bypassing VS's fixed built-in token-type→classification table. One instance is shared
            // by both pipelines so it sees requests (VS→Server) and their responses (Server→VS).
            var semanticTokensInterceptor = new SemanticTokensClassificationInterceptor(
                SemanticTokenClassificationStore.Instance, _loggerFactory.CreateLogger<SemanticTokensClassificationInterceptor>());

            // Tracks .cs files created by the scaffold code action and injects a
            // reqnroll/projectFiles delta before the server sees textDocument/didOpen.
            // Uses a lazy reference because ProjectMonitor is set well after the pipe exists.
            var scaffoldInterceptor = new ScaffoldTrackingInterceptor(
                () => ProjectMonitor, _loggerFactory.CreateLogger<ScaffoldTrackingInterceptor>());

            // Watches the server's reqnroll/refreshCodeLens push and invalidates the C# code
            // lenses so VS re-queries updated usage/match counts after a binding edit or build.
            _codeLensRefreshInterceptor = new CodeLensRefreshInterceptor(
                _stepCodeLensState, _loggerFactory.CreateLogger<CodeLensRefreshInterceptor>());

            // Drives DocumentActivationState's didOpen/didClose transitions (issue #85) and, in
            // the activation-before-open case, sends reqnroll/documentActivated itself right
            // after re-forwarding didOpen. Uses a lazy reference for the same reason as above:
            // this pipe doesn't exist yet at the point the interceptor is constructed.
            var documentActivationInterceptor = new DocumentActivationTrackingInterceptor(
                _activationState, () => _interceptingPipe, _loggerFactory.CreateLogger<DocumentActivationTrackingInterceptor>());

            // Watches the shutdown request/response handshake so Dispose() knows whether a
            // graceful `exit` is safe to request instead of killing the process outright.
            _shutdownHandshakeInterceptor = new ShutdownHandshakeInterceptor(
                _loggerFactory.CreateLogger<ShutdownHandshakeInterceptor>());

            // Send pipeline:   VS → [logger, semanticTokens, scaffold, documentActivation, shutdownHandshake] → Server
            // Receive pipeline: Server → [logger, semanticTokens, scaffold, codeLensRefresh, shutdownHandshake, telemetry] → VS
            // codeLensRefresh is receive-only: it acts solely on the server's reqnroll/refreshCodeLens
            // push. It used to sit on the send pipeline as well, watching .cs didChange to invalidate
            // per edit, but that path was removed for issue #156 and the class has been a no-op on
            // send ever since; the server-pushed signal (issue #343) covers the same need.
            // shutdownHandshake is on both pipelines: send captures the outgoing shutdown request id;
            // receive watches for the matching response.
            var sendInterceptors = new ILspMessageInterceptor[]
                { _inspectorLogger, semanticTokensInterceptor, scaffoldInterceptor, documentActivationInterceptor, _shutdownHandshakeInterceptor };

            // Telemetry interceptor: lazy reference because TelemetryTransmitter is resolved
            // from MEF on the main thread during OnServerInitializationResultAsync.
            var telemetryInterceptor = new TelemetryEventInterceptor(
                () => TelemetryTransmitter, _loggerFactory.CreateLogger<TelemetryEventInterceptor>());
            var receiveInterceptors = new ILspMessageInterceptor[]
                { _inspectorLogger, semanticTokensInterceptor, scaffoldInterceptor, _codeLensRefreshInterceptor, _shutdownHandshakeInterceptor, telemetryInterceptor };

            _interceptingPipe = new LspInterceptingPipe(
                rawPipe, sendInterceptors, receiveInterceptors, _loggerFactory.CreateLogger<LspInterceptingPipe>());
            // Pass CancellationToken.None: the pumps must live for the entire connection
            // lifetime, not just for the duration of this async creation call. The pipe's
            // own internal CTS (cancelled in Dispose) provides the shutdown signal.
            await _interceptingPipe.StartAsync(CancellationToken.None).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspServerConnectionService: failed to start server.");
            return false;
        }
    }

    /// <summary>
    /// Begins asynchronous, best-effort teardown of the server process and intercepting pipe.
    /// Returns immediately; the actual shutdown work runs on the thread pool (see remarks).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation(
            "LspServerConnectionService: instance #{InstanceId} disposing — shutting down server connection. " +
            "This is one-way: OnInitializedAsync resolves this singleton once per extension instance, so nothing " +
            "recreates it if the process keeps running (issue #555).", _instanceId);

        // ProjectMonitor is disposed by ReqnrollLanguageClient.Dispose (UI-thread-bound, COM event
        // unsubscription) whenever the provider deactivates — not here, since this service's Dispose
        // may run off the UI thread at extension unload and VsProjectEventMonitor.Dispose() asserts
        // ThreadHelper.ThrowIfNotOnUIThread(). It is set to null there too.

        var shutdownHandshakeInterceptor = _shutdownHandshakeInterceptor;
        var interceptingPipe             = _interceptingPipe;
        var inspectorLogger              = _inspectorLogger;
        var serverProcess                = _serverProcess;
        var childJob                     = _childJob;

        // Stops the pending debounce timer so a queued CodeLens invalidation can't fire against
        // torn-down state after the connection is gone.
        _codeLensRefreshInterceptor?.Dispose();
        _codeLensRefreshInterceptor = null;

        _interceptingPipe = null;
        _inspectorLogger  = null;
        _serverProcess    = null;
        _childJob         = null;

        // Dispose() may run on VS's UI thread (see ReqnrollLanguageClient.Dispose()'s
        // ThreadHelper.ThrowIfNotOnUIThread()), so the graceful-exit-then-kill sequence below must
        // not be awaited here — Task.Run hands it to the thread pool so nothing runs synchronously
        // on the caller's thread. Dispose() keeps returning immediately, as before. ChildProcessJob
        // remains the safety net for VS itself terminating early.
        FireAndForgetExtensions.FireAndForget(
            () => ShutdownServerAsync(shutdownHandshakeInterceptor, interceptingPipe, serverProcess, inspectorLogger, childJob),
            _logger, nameof(ShutdownServerAsync));
    }

    /// <summary>
    /// Terminates the server process, preferring a graceful <c>shutdown</c>/<c>exit</c> negotiation
    /// over a hard <c>Kill()</c>. Uses the handshake already observed on this connection if VS's own
    /// client sent one; otherwise initiates <c>shutdown</c> itself, since VS's client cannot be
    /// relied on to do so during this teardown path.
    /// </summary>
    private async Task ShutdownServerAsync(
        ShutdownHandshakeInterceptor? shutdownHandshakeInterceptor,
        LspInterceptingPipe? interceptingPipe,
        Process? serverProcess,
        LspInspectorLogger? inspectorLogger,
        ChildProcessJob? childJob)
    {
        try
        {
            var shutdownObserved = shutdownHandshakeInterceptor?.ShutdownObserved ?? false;

            // VS's own client did not send `shutdown` on this connection. Rather than wait on a
            // request that's been confirmed not to arrive, send it ourselves on the still-live
            // pipe — we're a legitimate LSP client from the server's point of view — and treat any
            // response (success or error) as license to proceed to `exit`, per the LSP spec.
            if (!shutdownObserved && interceptingPipe is not null)
            {
                _logger.LogInformation(
                    "LspServerConnectionService: shutdown not observed from VS's client — sending our own shutdown request.");

                using var shutdownCts = new CancellationTokenSource(ShutdownRequestTimeoutMs);
                await interceptingPipe.SendRequestToServerAsync("shutdown", null, shutdownCts.Token)
                    .ConfigureAwait(false);

                if (shutdownCts.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "LspServerConnectionService: our own shutdown request did not receive a response within {TimeoutMs}ms; falling back to Kill().",
                        ShutdownRequestTimeoutMs);
                }
                else
                {
                    shutdownObserved = true;
                    _logger.LogInformation(
                        "LspServerConnectionService: server responded to our own shutdown request.");
                }
            }

            if (shutdownObserved && interceptingPipe is not null && serverProcess is not null)
            {
                // Per the LSP spec, `exit` is only valid after a `shutdown` response was received.
                // Written directly onto the server-bound stream (bypassing the VS-facing pipe) so
                // it goes out before the pipe below is torn down.
                try
                {
                    await interceptingPipe.SendNotificationToServerAsync("exit", null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "LspServerConnectionService: failed to send exit notification; falling back to Kill().");
                }
            }

            interceptingPipe?.Dispose();
            inspectorLogger?.Dispose();

            if (shutdownObserved && serverProcess is not null && serverProcess.WaitForExit(GracefulExitTimeoutMs))
            {
                _logger.LogInformation(
                    "LspServerConnectionService: server exited gracefully (PID {ProcessId}).", serverProcess.Id);
            }
            else
            {
                if (shutdownObserved)
                    _logger.LogWarning(
                        "LspServerConnectionService: server did not self-terminate within {TimeoutMs}ms of `exit`; killing.",
                        GracefulExitTimeoutMs);

                try { serverProcess?.Kill(); } catch { /* best-effort */ }
            }
        }
        finally
        {
            serverProcess?.Dispose();
            // Disposing the Job Object closes its last handle, which triggers
            // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE — must happen only after the process has already
            // exited or been killed above, never while a graceful exit is still in flight.
            childJob?.Dispose();
        }
    }
}
