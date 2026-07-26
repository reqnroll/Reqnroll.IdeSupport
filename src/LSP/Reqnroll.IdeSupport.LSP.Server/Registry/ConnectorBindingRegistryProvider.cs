using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Registry;

/// <summary>
/// Per-project <see cref="IBindingRegistryProvider"/> backed by
/// <see cref="ConnectorDiscoveryService"/>.
/// <para>
/// One instance lives in <see cref="LspReqnrollProject.Properties"/> (keyed by
/// <c>typeof(ConnectorBindingRegistryProvider)</c>), created by
/// <see cref="BindingRegistryProviderRouter"/> when a project is discovered.
/// </para>
/// <para>
/// Discovery runs are debounced (500 ms) and cancellable: calling
/// <see cref="TriggerRefresh"/> while a run is in flight cancels it and starts a fresh one.
/// When a run succeeds the last-good registry is replaced atomically and
/// <see cref="BindingRegistryChanged"/> is raised.  When a run fails the last-good registry
/// is kept and an error is logged; the registry is never replaced with
/// <see cref="ProjectBindingRegistry.Invalid"/> after it has been successfully populated once.
/// </para>
/// </summary>
public sealed class ConnectorBindingRegistryProvider : IBindingRegistryProvider, IDisposable
{
    private const int DebounceMilliseconds = 500;

    private readonly LspReqnrollProject _project;
    private readonly IConnectorDiscoveryService _discoveryService;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetryService;

    // Last-good state.  Volatile so readers always see the latest write.
    private volatile ProjectBindingRegistry _current = ProjectBindingRegistry.Invalid;
    private string _lastHash = string.Empty;
    private bool _isFirstRun = true;

    // Serialises the read-modify-write on _current so two concurrent ApplyRoslynFileUpdateAsync
    // calls (e.g. didChange edits on different files) don't silently drop each other's changes,
    // and coordinates with RunDiscoveryAsync's _current write on the connector-run path.
    private readonly SemaphoreSlim _currentLock = new(1, 1);

    // In-flight run guard.
    private readonly object _cts_lock = new();
    private CancellationTokenSource? _cts;

    // bool arg = isFullReplacement: true for connector runs, false for Roslyn per-file patches.
    private event EventHandler<bool>? _bindingRegistryChanged;

    /// <summary>
    /// Creates a provider backed by the default connector-based discovery service
    /// (generic/custom connector selected per project configuration). This is the single place
    /// that wiring happens, so callers (e.g. <see cref="BindingRegistryProviderRouter"/>) never
    /// need to construct <see cref="ConnectorDiscoveryService"/>/<see cref="OutProcReqnrollConnectorFactory"/>
    /// themselves.
    /// </summary>
    public ConnectorBindingRegistryProvider(
        LspReqnrollProject project, IIdeSupportLogger logger, ILspTelemetryService? telemetryService = null)
        : this(project, CreateDefaultDiscoveryService(logger), logger, telemetryService)
    {
    }

    private static IConnectorDiscoveryService CreateDefaultDiscoveryService(IIdeSupportLogger logger) =>
        new ConnectorDiscoveryService(logger, new OutProcReqnrollConnectorFactory(logger));

    /// <summary>
    /// Creates a provider backed by a caller-supplied discovery service.  Used by tests to
    /// substitute discovery so the debounce/cancellation/swap behaviour can be verified in
    /// isolation from the out-of-process connector.
    /// </summary>
    public ConnectorBindingRegistryProvider(
        LspReqnrollProject project,
        IConnectorDiscoveryService discoveryService,
        IIdeSupportLogger logger)
        : this(project, discoveryService, logger, null)
    {
    }

    /// <summary>
    /// Creates a provider backed by a caller-supplied discovery service and telemetry service.
    /// </summary>
    public ConnectorBindingRegistryProvider(
        LspReqnrollProject project,
        IConnectorDiscoveryService discoveryService,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService)
    {
        _project          = project;
        _logger           = logger;
        _discoveryService = discoveryService;
        _telemetryService = telemetryService;
    }

    // ── IBindingRegistryProvider ──────────────────────────────────────────────

    /// <inheritdoc/>
    public ProjectBindingRegistry Current => _current;

    /// <inheritdoc/>
    public event EventHandler<bool>? BindingRegistryChanged
    {
        add    => _bindingRegistryChanged += value;
        remove => _bindingRegistryChanged -= value;
    }

    // ── Public control ────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a debounced discovery run.  Any in-flight run is cancelled immediately;
    /// the new run starts after <c>500 ms</c> to absorb rapid successive triggers (e.g.
    /// several output files being written by a build).
    /// </summary>
    public void TriggerRefresh()
    {
        CancellationTokenSource? oldCts;
        CancellationTokenSource  newCts;

        lock (_cts_lock)
        {
            oldCts = _cts;
            newCts = new CancellationTokenSource();
            _cts   = newCts;
        }

        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = Task.Run(() => RunDiscoveryAsync(newCts.Token), newCts.Token);
    }

    /// <summary>
    /// Applies an immediate, source-level (Roslyn) binding update for a single C# file on top of
    /// the current registry, replacing only that file's step definitions and hooks (Roslyn/C#
    /// source-level binding discovery).
    /// </summary>
    /// <remarks>
    /// This is the in-process counterpart to the out-of-process reflection connector: it gives
    /// instant feedback as the user edits a step-definition file, without waiting for a build.
    /// The patch is layered on top of <see cref="Current"/> and intentionally does <b>not</b>
    /// advance the connector's last-good hash, so the next successful connector run (after a real
    /// build, whose assembly hash differs) replaces the whole registry with the authoritative
    /// post-build result. If no build has happened, the connector run is a hash-match no-op and
    /// the Roslyn patch survives.
    /// </remarks>
    public async Task ApplyRoslynFileUpdateAsync(CSharpStepDefinitionFile file)
    {
        await _currentLock.WaitAsync().ConfigureAwait(false);
        ProjectBindingRegistry updated;
        ProjectBindingRegistry previous;
        try
        {
            previous = _current;
            updated = await previous.ReplaceBindings(file).ConfigureAwait(false);
            _current = updated;
        }
        finally
        {
            _currentLock.Release();
        }

        // Skip the notification entirely when no binding's matched expression actually changed
        // (e.g. a method-body or comment edit). Publishing here drives feature-file reparsing
        // downstream (BindingRegistryChangedHandler), which can only produce a different result
        // when a binding's expression was added, removed, or edited -- so there's nothing for
        // that pipeline to do, and running it anyway would just burn CPU on every keystroke.
        if (!ProjectBindingRegistry.HasExpressionChanges(previous, updated, file.FullName))
            return;

        _bindingRegistryChanged?.Invoke(this, false);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Cancels and disposes the provider's background discovery loop.</summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_cts_lock)
        {
            cts  = _cts;
            _cts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
        _currentLock.Dispose();
    }

    // ── Discovery loop ────────────────────────────────────────────────────────

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            // Debounce: absorb file-system churn from incremental builds.
            await Task.Delay(DebounceMilliseconds, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var (newRegistry, newHash) = await Task
                .Run(() => _discoveryService.RunDiscovery(_project, _current, _lastHash, ct), ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Skip the swap if nothing changed (hash matches means RunDiscovery returned lastGood).
            if (newHash == _lastHash)
            {
                // Lightweight telemetry: connector hash-noop rate (membership index / telemetry
                // design §4.2).
                _telemetryService?.SendEvent("Reqnroll Discovery executed", new()
                {
                    ["DiscoverySource"] = "Connector",
                    ["HashMatched"] = true,
                    ["TriggerContext"] = _isFirstRun ? "projectLoad" : "build",
                });
                if (_isFirstRun) _isFirstRun = false;
                return;
            }

            _lastHash = newHash;

            await _currentLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _current = newRegistry;
            }
            finally
            {
                _currentLock.Release();
            }

            // Telemetry: connector discovery event (membership index / telemetry design §2.2).
            var triggerContext = _isFirstRun ? "projectLoad" : "build";
            _isFirstRun = false;
            // StepArgumentTransformations are not reported: the connector surfaces them, but
            // ProjectBindingRegistry does not model them, so there is no count to emit here.
            _telemetryService?.SendEvent("Reqnroll Discovery executed", new()
            {
                ["DiscoverySource"] = "Connector",
                ["TriggerContext"] = triggerContext,
                ["IsFailed"] = false,
                ["StepDefinitionCount"] = newRegistry.StepDefinitions.Length,
                ["HookCount"] = newRegistry.Hooks.Length,
                ["ProjectTargetFramework"] = _project.TargetFrameworkMonikers,
            });

            _bindingRegistryChanged?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            // Normal: a newer TriggerRefresh cancelled this run. Not a failure — no telemetry.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                $"[{_project.ProjectName}] Unexpected error during binding discovery: {ex.Message}");

            // Telemetry: connector discovery failure (membership index / telemetry design §2.2
            // IsFailed / §4.3 error recovery).
            // _isFirstRun is intentionally NOT cleared here: a failed initial load is still a load,
            // so a subsequent (hopefully successful) run continues to report "projectLoad".
            _telemetryService?.SendEvent("Reqnroll Discovery executed", new()
            {
                ["DiscoverySource"] = "Connector",
                ["TriggerContext"] = _isFirstRun ? "projectLoad" : "build",
                ["IsFailed"] = true,
                ["ErrorMessage"] = ex.Message,
                ["ProjectTargetFramework"] = _project.TargetFrameworkMonikers,
            });
        }
    }
}
