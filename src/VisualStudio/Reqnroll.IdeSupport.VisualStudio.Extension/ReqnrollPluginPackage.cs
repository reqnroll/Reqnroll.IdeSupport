using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;
using IServiceProvider = System.IServiceProvider;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// VSSDK auto-load package for the extension: guarantees the extension activates whenever a
/// solution exists (see the <see cref="ProvideAutoLoadAttribute"/> below), independent of whether
/// a <c>.feature</c> file is the foreground editor. Also shows the Welcome/Upgrade dialog and
/// registers the extension install folder on VS's assembly binding path.
/// </summary>
// Startup-race avoidance: ProvideAutoLoad ensures the package loads when a solution exists, even when no
// .feature file is the foreground editor. Without this, the LSP server never starts on
// session restore if the foreground tab is a .cs file (scenario A).
[ProvideAutoLoad(
    UIContextGuids80.SolutionExists,
    PackageAutoLoadFlags.BackgroundLoad)]
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
// Put the extension install folder on VS's assembly binding path. Without this, VS can only
// load our assemblies by *path* (which MEF does via catalog.json) — but identity-based loads
// fail: the VsPackage itself (Assembly.Load of this assembly's strong name from the pkgdef)
// and the item/project template wizards (loaded by the template engine via the strong name in
// each .vstemplate's <WizardExtension>) both resolve by identity and otherwise get
// FileNotFound. The Microsoft.VisualStudio.Assembly manifest assets are not honored as
// codebases in the VSSDK+VisualStudio.Extensibility hybrid, so a BindingPath is required.
[ProvideBindingPath]
// Backs HookCodeLensCommands.vsct's <CommandFlag>CommandWellOnly</CommandFlag> button (issue
// #372): the hook-match-count CodeLens's Details popup invokes it, routed through this package's
// IOleCommandTarget below, exactly as Microsoft's own CodeLensOopSample does.
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(PackageGuidString)]
public sealed class ReqnrollPluginPackage : AsyncPackage, IOleCommandTarget
{
    /// <summary>The package's GUID, as registered with VS.</summary>
    public const string PackageGuidString = "8d5fe503-e038-4079-9e45-697e0dcb3758";

    private IIdeSupportLogger _logger = new IdeSupportNullLogger();
    private ITelemetryTransmitter? _telemetryTransmitter;
    private IOleCommandTarget? _nextCommandTarget;
    private DocumentInitializationMonitor? _documentInitializationMonitor;

    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // MEF-independent breadcrumb (issue #263): written via VS's own Activity Log
        // (Help > About > "View Activity Log" or %AppData%\...\ActivityLog.xml), so
        // package activation is observable even if the MEF-exported IIdeSupportLogger
        // below never resolves. Emitted before anything else so it survives whatever
        // happens next.
        TryLogActivity(isError: false, "InitializeAsync started (pre-MEF).");

        await base.InitializeAsync(cancellationToken, progress);

        // Resolve the shared MEF-exported IIdeSupportLogger (issue #84) rather than a private
        // ad-hoc SynchronousFileLogger + a second, unlistened-to TraceSource. Resolved early
        // (alongside ITelemetryTransmitter below) so it's available for the full package lifecycle.
        // If it never becomes available, falls back to the no-op logger but says so loudly via
        // ActivityLog, which doesn't depend on MEF and so stays observable even in that failure case.
        await ResolveLoggerAndTelemetryAsync(cancellationToken);

        _logger.LogInfo("ReqnrollPluginPackage: InitializeAsync started.");

        // Advise before the solution-load wait, not after: the restored .feature stubs we want to
        // observe are realized *during* restore, so a subscription taken afterwards would miss the
        // very transitions this is here to time (issue #533, phase 2). Diagnostic only — it never
        // forces a document to initialize.
        await AdviseDocumentInitializationMonitorAsync(cancellationToken);

        _logger.LogInfo("Waiting for solution load...");

        await WaitForSolutionLoadAsync(cancellationToken);

        // NOTE: We intentionally do NOT realize .feature stub frames here. Doing so at
        // solution load races with VS's own restore of feature tabs and spawns a second
        // LSP server process, leaving the editor bound to an unmatched server (no step
        // parameter coloring / no CodeLens usage counts). The LanguageServerProvider
        // activates the normal way (VS realizes a feature tab, or the user opens a
        // feature file), and ReqnrollLanguageClient.OnServerInitializationResultAsync
        // flushes any remaining stubs at the safe post-server-init point.
        // TODO: reinstate a non-racing/idempotent way to start the LSP when the
        // foreground tab is a .cs file and no feature file is open.

        _logger.LogInfo("Solution loaded.");

        // Show the Welcome (first install) or Upgrade (version change) dialog
        // if appropriate, after a short delay so VS can finish initializing.
        await RunWelcomeServiceAsync(cancellationToken);

        _logger.LogInfo("Package initialisation complete.");
    }

    /// <summary>
    /// Resolves <see cref="_logger"/> and <see cref="_telemetryTransmitter"/> from MEF.
    /// </summary>
    /// <remarks>
    /// Root cause of issue #263/#266: <see cref="AsyncPackage.GetServiceAsync(Type)"/> called with
    /// <see cref="SComponentModel"/> returns the <see cref="IComponentModel"/> service object
    /// itself — not some other <see cref="IServiceProvider"/> through which <c>SComponentModel</c>
    /// could be looked up again. The original code cast that result <c>as IServiceProvider</c>,
    /// which <see cref="IComponentModel"/> does not implement, so the cast silently produced null
    /// on every call, 100% reproducibly (confirmed live: 8/8 attempts null over 2s in a #266
    /// diagnostic run) — not the intermittent startup race originally suspected. Casting directly
    /// to <see cref="IComponentModel"/> and calling <see cref="IComponentModel.GetService{T}"/> is
    /// the correct pattern (contrast <see cref="VsUtils.ResolveMefDependency{T}"/>, which is for
    /// the different case of already holding some other <see cref="IServiceProvider"/> — e.g.
    /// <see cref="ServiceProvider.GlobalProvider"/> in <see cref="RunWelcomeServiceAsync"/> below —
    /// and querying it for <c>SComponentModel</c>).
    /// A short retry remains as a defensive safety net in case the component model genuinely isn't
    /// registered yet this early in activation, but is not expected to be needed in practice.
    /// </remarks>
    /// <remarks>
    /// Must run on the UI thread (issue #266 follow-up): the first-ever resolution of
    /// <see cref="ITelemetryTransmitter"/> in a session eagerly constructs <c>TelemetryTransmitter</c>,
    /// whose constructor calls <c>VersionProvider.GetVsVersion()</c> — which throws if not on the
    /// UI thread. MEF caches a faulted part permanently for the composition container's lifetime,
    /// so a single off-UI-thread first-touch here silently and permanently breaks telemetry (and
    /// anything transitively importing it, e.g. <see cref="RunWelcomeServiceAsync"/>'s <c>IIdeScope</c>
    /// resolution below) for the rest of the session. <see cref="InitializeAsync"/> runs on a
    /// background thread by default (<see cref="PackageAutoLoadFlags.BackgroundLoad"/>), so an
    /// explicit switch is required before touching MEF here — confirmed live: without it, this
    /// exact fault occurred and cascaded into the Welcome dialog never appearing.
    /// </remarks>
    private async Task ResolveLoggerAndTelemetryAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 4; // ~1 second
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                if (componentModel != null)
                {
                    if (_logger is IdeSupportNullLogger)
                        _logger = componentModel.GetService<IIdeSupportLogger>() ?? _logger;

                    _telemetryTransmitter ??= componentModel.GetService<ITelemetryTransmitter>();

                    if (!(_logger is IdeSupportNullLogger) && _telemetryTransmitter != null)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // Package is shutting down / initialization was cancelled — not a resolution
                // failure. Let it propagate instead of logging a misleading "threw" breadcrumb
                // and burning the remaining retry budget on a token that's already cancelled.
                throw;
            }
            catch (Exception ex)
            {
                TryLogActivity(isError: false, $"MEF resolution attempt {attempt}/{maxAttempts} threw: {ex}");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (_logger is IdeSupportNullLogger)
        {
            TryLogActivity(isError: true,
                "IIdeSupportLogger did not resolve via MEF after retries; falling back to a no-op " +
                "logger for the rest of this session. This class's LogInfo/LogWarning/LogException " +
                "calls will not appear in the extension log.");
        }
    }

    /// <summary>
    /// Writes a breadcrumb to VS's built-in Activity Log, independent of MEF/<see cref="_logger"/>,
    /// so this class's activation and MEF-resolution failures stay observable even when the
    /// MEF-exported logger itself is unavailable (issue #263). Best-effort: swallows failures
    /// since this is a diagnostic aid, not something that should abort package initialization.
    /// </summary>
    private static void TryLogActivity(bool isError, string message)
    {
        try
        {
            if (isError)
                ActivityLog.LogError(nameof(ReqnrollPluginPackage), message);
            else
                ActivityLog.LogInformation(nameof(ReqnrollPluginPackage), message);
        }
        catch
        {
            // Diagnostic-only; never let a logging failure affect package initialization.
        }
    }

    private async Task RunWelcomeServiceAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("RunWelcomeServiceAsync: starting.");
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var sp = ServiceProvider.GlobalProvider;

            // Resolve the next command target in the routing chain (issue #372): needed so this
            // package's IOleCommandTarget override — which handles only the hook-match-count
            // lens's own navigate-to-hook command — can forward everything else, exactly like
            // Microsoft's own CodeLensOopSample.
            _nextCommandTarget = await GetServiceAsync(typeof(IOleCommandTarget)) as IOleCommandTarget;

            // Resolve MEF services
            _logger.LogInfo("RunWelcomeServiceAsync: resolving IRegistryManager...");
            var registryManager = VsUtils.ResolveMefDependency<IRegistryManager>(sp);
            if (registryManager is null)
            {
                _logger.LogWarning("RunWelcomeServiceAsync: IRegistryManager not available, skipping.");
                return;
            }
            _logger.LogInfo("RunWelcomeServiceAsync: IRegistryManager resolved OK.");

            _logger.LogInfo("RunWelcomeServiceAsync: resolving IVersionProvider...");
            var versionProvider = VsUtils.ResolveMefDependency<IVersionProvider>(sp);
            if (versionProvider is null)
            {
                _logger.LogWarning("RunWelcomeServiceAsync: IVersionProvider not available, skipping.");
                return;
            }
            _logger.LogInfo("RunWelcomeServiceAsync: IVersionProvider resolved OK. Version=" + versionProvider.GetExtensionVersion());

            _logger.LogInfo("RunWelcomeServiceAsync: resolving IFileSystemForIDE...");
            var fileSystem = VsUtils.ResolveMefDependency<IFileSystemForIDE>(sp);
            if (fileSystem is null)
            {
                _logger.LogWarning("RunWelcomeServiceAsync: IFileSystemForIDE not available, skipping.");
                return;
            }
            _logger.LogInfo("RunWelcomeServiceAsync: IFileSystemForIDE resolved OK.");

            _logger.LogInfo("RunWelcomeServiceAsync: resolving IIdeScope...");
            var ideScope = VsUtils.ResolveMefDependency<IIdeScope>(sp);
            if (ideScope is null)
            {
                _logger.LogWarning("RunWelcomeServiceAsync: IIdeScope not available, skipping.");
                return;
            }
            _logger.LogInfo("RunWelcomeServiceAsync: IIdeScope resolved OK.");

            // Create the dialog service (manual creation, not MEF-exported)
            _logger.LogInfo("RunWelcomeServiceAsync: resolving IVsUIShell...");
            var vsUiShell = sp.GetService(typeof(SVsUIShell)) as IVsUIShell;
            if (vsUiShell is null)
            {
                _logger.LogWarning("RunWelcomeServiceAsync: IVsUIShell not available, skipping.");
                return;
            }
            _logger.LogInfo("RunWelcomeServiceAsync: IVsUIShell resolved OK.");

            var telemetryService = ideScope.TelemetryService;
            // Restores the legacy "Extension loaded" signal (previously fired from
            // MonitoringService.MonitorOpenProjectSystem, which also triggered the welcome flow
            // itself in the old architecture — here that triggering already happens explicitly
            // below via WelcomeService, so this call is telemetry-only, issue #255/#259).
            telemetryService.MonitorOpenProjectSystem(ideScope);
            var dialogService = new VsWizardDialogService(vsUiShell, telemetryService);

            _logger.LogInfo("RunWelcomeServiceAsync: creating WelcomeService...");
            var welcomeService = new WelcomeService(
                registryManager, versionProvider, dialogService, fileSystem);

            _logger.LogInfo("RunWelcomeServiceAsync: calling OnIdeScopeActivityStarted...");
            welcomeService.OnIdeScopeActivityStarted(ideScope);
            _logger.LogInfo("RunWelcomeServiceAsync: OnIdeScopeActivityStarted returned (dialog scheduled with 7s delay).");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "RunWelcomeServiceAsync: Failed");
        }
    }

    /// <summary>
    /// Subscribes <see cref="DocumentInitializationMonitor"/> to the Running Document Table so the
    /// document inventory and every stub realization are logged (issue #533, phase 2).
    /// Best-effort: a failure here must not abort package initialization.
    /// </summary>
    private async Task AdviseDocumentInitializationMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var rdt = await GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            _documentInitializationMonitor = DocumentInitializationMonitor.TryAdvise(rdt, _logger);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "ReqnrollPluginPackage: could not advise the document initialization monitor.");
        }
    }

    private async Task WaitForSolutionLoadAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        if (solution is null)
            return;

        // Poll until the solution is open. ProvideAutoLoad(BackgroundLoad) guarantees the
        // solution exists by the time InitializeAsync runs, but projects may still be loading
        // in the background. We use a brief polling loop as a practical gate.
        const int maxAttempts = 40; // ~10 seconds
        for (int i = 0; i < maxAttempts; i++)
        {
            // The VSPSROPID_IsOpen value is 0x0000000B per the VS SDK headers.
            solution.GetProperty(0x0000000B, out var isOpen);
            if (isOpen is true)
                return;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }

        _logger.LogInfo("WaitForSolutionLoadAsync: max attempts reached, proceeding anyway.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _documentInitializationMonitor?.Dispose();
            _documentInitializationMonitor = null;

            if (_telemetryTransmitter is IAsyncDisposable d)
            {
                ThreadHelper.JoinableTaskFactory.Run(() => d.DisposeAsync().AsTask());
            }
        }
        base.Dispose(disposing);
    }

    // ── IOleCommandTarget: hook-match-count CodeLens navigation (issue #372) ──────────────────
    //
    // The classic CodeLens Details popup (see HookCodeLensDataPoint.GetDetailsAsync in the
    // VSSDKIntegration project) invokes each entry's NavigationCommand via raw OLE command
    // routing rather than a VS.Extensibility command — CodeLensDetailEntryCommand only supports a
    // CommandSet+CommandId pair on Windows (see Microsoft.VisualStudio.Language.CodeLens.Remoting).
    // Mirrors Microsoft's own CodeLensOopSample (CodeLensOopProviderPackage).

    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (pguidCmdGroup == HookCodeLensCommandIds.CommandSet && cCmds > 0
            && prgCmds[0].cmdID == HookCodeLensCommandIds.NavigateToHookCommandId)
        {
            prgCmds[0].cmdf |= (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_INVISIBLE);
            return VSConstants.S_OK;
        }

        return _nextCommandTarget?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
               ?? (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (pguidCmdGroup == HookCodeLensCommandIds.CommandSet && nCmdID == HookCodeLensCommandIds.NavigateToHookCommandId)
        {
            if (pvaIn == IntPtr.Zero)
                return VSConstants.S_FALSE;

            var argsObject = Marshal.GetObjectForNativeVariant(pvaIn);
            if (argsObject is string encoded)
                NavigateToHook(encoded);

            return VSConstants.S_OK;
        }

        return _nextCommandTarget?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
               ?? (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    /// <summary>
    /// Opens (or activates) the hook definition encoded as <c>"uri|line0|char0"</c> by
    /// <c>HookCodeLensDataPoint.GetDetailsAsync</c>, then places the caret at that position.
    /// </summary>
    private void NavigateToHook(string encoded)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var parts = encoded.Split('|');
            if (parts.Length != 3
                || !Uri.TryCreate(parts[0], UriKind.Absolute, out var uri) || !uri.IsFile
                || !int.TryParse(parts[1], out var line0)
                || !int.TryParse(parts[2], out var char0))
            {
                _logger.LogWarning($"ReqnrollPluginPackage.NavigateToHook: malformed args '{encoded}'.");
                return;
            }

            var filePath = uri.LocalPath;
            VsShellUtilities.OpenDocument(
                this, filePath, VSConstants.LOGVIEWID.TextView_guid,
                out _, out _, out var frame);

            var textView = VsShellUtilities.GetTextView(frame);
            if (textView is null)
                return;

            // IVsTextView positions are 0-based, matching line0/char0 already.
            textView.SetCaretPos(line0, char0);
            textView.CenterLines(line0, 1);
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "ReqnrollPluginPackage.NavigateToHook: failed");
        }
    }
}
