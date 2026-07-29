using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;
using Reqnroll.IdeSupport.VisualStudio.Extension.NavigationBar;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
#pragma warning disable VSEXTPREVIEW_LSP

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// The VS.Extensibility <see cref="LanguageServerProvider"/> for <c>.feature</c> files: hands VS
/// the LSP connection, and on successful initialization wires up all the runtime-created command
/// services/renderers (Find Step Usages, Go to Hooks, Rename Step, Step Code Lens, Comment Toggle,
/// Navigation Bar) and the DTE-based <see cref="VsProjectEventMonitor"/>.
/// </summary>
[VisualStudioContribution]
internal class ReqnrollLanguageClient : LanguageServerProvider
{
    private readonly ILogger<ReqnrollLanguageClient> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FindStepUsagesState _findStepUsagesState;
    private readonly FindUnusedStepDefinitionsState _findUnusedStepDefinitionsState;
    private readonly GoToHooksState _goToHooksState;
    private readonly GoToMatchingScenariosState _goToMatchingScenariosState;
    private readonly StepCodeLensState _stepCodeLensState;
    private readonly CommentToggleState _commentToggleState;
    private readonly RenameStepState _renameStepState;
    private readonly LspServerConnectionService _connectionService;
    private GherkinNavigationBarSymbolService? _navigationBarSymbolService;

    /// <summary>Creates the language client, resolving the shared state holders and the already-launching connection service.</summary>
    public ReqnrollLanguageClient(
        ExtensionCore container,
        VisualStudioExtensibility extensibilityObject,
        ILogger<ReqnrollLanguageClient> logger,
        ILoggerFactory loggerFactory,
        FindStepUsagesState findStepUsagesState,
        FindUnusedStepDefinitionsState findUnusedStepDefinitionsState,
        GoToHooksState goToHooksState,
        GoToMatchingScenariosState goToMatchingScenariosState,
        StepCodeLensState stepCodeLensState,
        CommentToggleState commentToggleState,
        RenameStepState renameStepState,
        LspServerConnectionService connectionService)
        : base(container, extensibilityObject)
    {
        _logger                         = logger;
        _loggerFactory                  = loggerFactory;
        _findStepUsagesState            = findStepUsagesState;
        _findUnusedStepDefinitionsState = findUnusedStepDefinitionsState;
        _goToHooksState                 = goToHooksState;
        _goToMatchingScenariosState     = goToMatchingScenariosState;
        _stepCodeLensState              = stepCodeLensState;
        _commentToggleState             = commentToggleState;
        _renameStepState                = renameStepState;
        // LspServerConnectionService is a singleton already resolved (and its eager server launch
        // already kicked off) by ExtensionEntrypoint.OnInitializedAsync well before this class is
        // constructed — this constructor param just retrieves the same instance. It is NOT this
        // constructor that triggers the eager launch: an earlier version of this comment assumed
        // VS.Extensibility constructs ReqnrollLanguageClient at extension load, independent of
        // .feature-file activation. Three logged VS sessions disproved that — this class is only
        // constructed when VS actually activates the LanguageServerProvider (.feature file open),
        // same as before this change. See ExtensionEntrypoint.OnInitializedAsync's remarks for the
        // corrected mechanism and the log evidence.
        _connectionService   = connectionService;
        _logger.LogInformation(
            "ReqnrollLanguageClient: instance created. VS extension loaded. Assembly: {AssemblyLocation}",
            typeof(ReqnrollLanguageClient).Assembly.Location);
    }

    /// <inheritdoc />
    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration =>
        new("Reqnroll Language Client",
            new[]
            {
                DocumentFilter.FromDocumentType(GherkinDocumentType.GherkinDocument),
            });

    /// <inheritdoc />
    public override async Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ReqnrollLanguageClient: CreateServerConnectionAsync called — awaiting eager connection.");

        // Startup (process launch + pipe construction) was kicked off eagerly when
        // LspServerConnectionService was constructed — see its remarks. This just awaits
        // whatever is already in flight (or already completed) rather than starting it now.
        var pipe = await _connectionService.GetConnectionAsync().ConfigureAwait(false);

        if (pipe is null)
        {
            _logger.LogError("ReqnrollLanguageClient: LSP server connection unavailable. Disabling.");
            Enabled = false;
            return null;
        }

        return pipe;
    }

    /// <inheritdoc />
    public override async Task OnServerInitializationResultAsync(
        ServerInitializationResult serverInitializationResult,
        LanguageServerInitializationFailureInfo? initializationFailureInfo,
        CancellationToken cancellationToken)
    {
        if (serverInitializationResult == ServerInitializationResult.Failed)
        {
            var failMsg = initializationFailureInfo?.StatusMessage
                          ?? initializationFailureInfo?.Exception?.Message
                          ?? "(none)";
            _logger.LogError(
                "ReqnrollLanguageClient: server initialization failed. Info: {FailMessage}", failMsg);
            Enabled = false;
            return;
        }

        _logger.LogInformation(
            "ReqnrollLanguageClient: server initialized successfully ({ServerInitializationResult}).",
            serverInitializationResult);

        // Start monitoring VS project events and flush the current solution state.
        var interceptingPipe = _connectionService.InterceptingPipe;
        if (interceptingPipe is not null)
        {
            // GoToHooksService and FindStepUsagesService use only
            // LspInterceptingPipe + ILogger<T> — no COM, safe here.
            _findStepUsagesState.Service            = new FindStepUsagesService(interceptingPipe, _loggerFactory.CreateLogger<FindStepUsagesService>());
            _findUnusedStepDefinitionsState.Service = new FindUnusedStepDefinitionsService(interceptingPipe, _loggerFactory.CreateLogger<FindUnusedStepDefinitionsService>());
            _goToHooksState.Service                 = new GoToHooksService(interceptingPipe, _loggerFactory.CreateLogger<GoToHooksService>());
            _goToMatchingScenariosState.Service     = new GoToMatchingScenariosService(interceptingPipe, _loggerFactory.CreateLogger<GoToMatchingScenariosService>());
            _stepCodeLensState.Service              = new StepCodeLensService(interceptingPipe, _loggerFactory.CreateLogger<StepCodeLensService>());
            _commentToggleState.Service             = new CommentToggleService(interceptingPipe, _loggerFactory.CreateLogger<CommentToggleService>());
            _renameStepState.Service                 = new RenameStepService(interceptingPipe, _loggerFactory.CreateLogger<RenameStepService>());
            _navigationBarSymbolService              = new GherkinNavigationBarSymbolService(interceptingPipe, _loggerFactory.CreateLogger<GherkinNavigationBarSymbolService>());

            // Set the VSSDK command filter redirect so the keyboard shortcut interception
            // for Edit.CommentSelection/UncommentSelection/ToggleLineComment calls our service.
            CommentToggleRedirect.ToggleCommentAsync = _commentToggleState.Service.ToggleCommentAsync;

            // Set the VSSDK drop-down bar client redirect so the
            // Navigation Bar can fetch the Feature/Scenario/Step symbol tree.
            NavigationBarRedirect.FetchDocumentSymbolsAsync = _navigationBarSymbolService.FetchSymbolsAsync;

            try
            {
                // VsProjectEventMonitor and FindStepUsagesRenderer both access VS COM services
                // (DTE, SVsFindAllReferences).  VS.Extensibility may call this method on a
                // background thread (e.g. the JSON-RPC receive thread), so we marshal explicitly.
                await ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync(cancellationToken);

                var serviceProvider = ServiceProvider.GlobalProvider;
                _connectionService.TelemetryTransmitter = ResolveMefService<ITelemetryTransmitter>(serviceProvider);
                _logger.LogInformation(
                    "ReqnrollLanguageClient: ITelemetryTransmitter resolved: {Resolved}",
                    _connectionService.TelemetryTransmitter is not null ? "yes" : "no");
                _findStepUsagesState.Renderer            = new FindStepUsagesRenderer(serviceProvider, _loggerFactory.CreateLogger<FindStepUsagesRenderer>());
                _findUnusedStepDefinitionsState.Renderer = new FindUnusedStepDefinitionsRenderer(serviceProvider, _loggerFactory.CreateLogger<FindUnusedStepDefinitionsRenderer>());

                // Reuse the Find Step Definition Usages / Find All References components for the code-lens click action.
                _stepCodeLensState.FindUsagesService  = _findStepUsagesState.Service;
                _stepCodeLensState.FindUsagesRenderer = _findStepUsagesState.Renderer;

                // VS.Extensibility can call this method more than once per session (issue #156):
                // a second activation must not leave the first ProjectMonitor's DTE event
                // subscriptions (SolutionEvents/BuildEvents/WindowEvents/
                // IVsTrackProjectDocumentsEvents2) live alongside the new one — that would double
                // every subsequent notification to the server and leak the abandoned instance's
                // COM event-sink references for the rest of the VS session (issue #320). Dispose()
                // is idempotent/UI-thread-safe, so this is a no-op on the first (normal) call.
                _connectionService.ProjectMonitor?.Dispose();

                _logger.LogInformation("ReqnrollLanguageClient: creating VsProjectEventMonitor.");
                _connectionService.ProjectMonitor = new VsProjectEventMonitor(
                    interceptingPipe, _loggerFactory.CreateLogger<VsProjectEventMonitor>(), serviceProvider,
                    _connectionService.ActivationState);

                _logger.LogInformation("ReqnrollLanguageClient: sending initial projects.");
                await _connectionService.ProjectMonitor
                    .SendInitialProjectsAsync(cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("ReqnrollLanguageClient: flushing .feature stub frames.");
                await VsStubFrameInitializer.ForceInitFeatureStubsAsync(
                        ServiceProvider.GlobalProvider, _logger, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("ReqnrollLanguageClient: initial project flush complete.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReqnrollLanguageClient: could not start project monitor.");
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool isDisposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (isDisposing)
        {
            _logger.LogInformation("ReqnrollLanguageClient: disposing — shutting down server connection.");

            // ProjectMonitor is UI-thread/COM-bound, so it is disposed here (this method already
            // asserts the UI thread above) rather than in LspServerConnectionService.Dispose.
            _connectionService.ProjectMonitor?.Dispose();
            _connectionService.ProjectMonitor = null;

            _findStepUsagesState.Service             = null;
            _findStepUsagesState.Renderer            = null;
            _findUnusedStepDefinitionsState.Service  = null;
            _findUnusedStepDefinitionsState.Renderer = null;
            _goToHooksState.Service                  = null;
            _goToMatchingScenariosState.Service      = null;
            _stepCodeLensState.Service           = null;
            _stepCodeLensState.FindUsagesService  = null;
            _stepCodeLensState.FindUsagesRenderer = null;
            _commentToggleState.Service = null;
            _renameStepState.Service = null;
            _navigationBarSymbolService = null;
            NavigationBarRedirect.FetchDocumentSymbolsAsync = null;

            // _connectionService itself is NOT disposed here: it's a DI-owned singleton whose
            // lifetime spans the whole extension session, not just this provider instance.
            //
            // This method may in fact never run at all: VS talks to the generated
            // ILanguageServerProviderService wrapper (decompiled from
            // Microsoft.VisualStudio.Extensibility.dll), whose Dispose() is an empty method body
            // that never forwards to this class. Confirmed no "disposing" log line from this method
            // has ever appeared across any recorded VS session. LspServerConnectionService disposal
            // is instead wired in ExtensionEntrypoint.OnInitializedAsync to
            // Microsoft.VisualStudio.Shell.VsShellUtilities.ShutdownToken — the classic, static,
            // shell-level signal confirmed by logging to actually fire on window close (unlike
            // ExtensionCore.ShutdownToken, registered there too but empirically dead).
        }

        base.Dispose(isDisposing);
    }

    // ── MEF resolution helper ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a MEF-exported service from the VS component model.
    /// </summary>
    private static T? ResolveMefService<T>(IServiceProvider serviceProvider) where T : class
    {
        try
        {
            var componentModel = (IComponentModel)serviceProvider.GetService(typeof(SComponentModel));
            return componentModel?.GetService<T>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "ReqnrollLanguageClient: Failed to resolve MEF service {0}: {1}",
                typeof(T).Name, ex.Message);
            return null;
        }
    }
}
