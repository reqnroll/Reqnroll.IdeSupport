using Reqnroll.IdeSupport.Common;
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

    // Distinct from "_current is populated": _current can also become non-Invalid via a Roslyn
    // per-file patch (ApplyRoslynFileUpdateAsync), e.g. from textDocument/didOpen on an unbuilt
    // project. This tracks specifically whether the out-of-process connector has ever
    // successfully loaded real bindings from a compiled DLL (RunDiscoveryAsync's non-hash-match
    // path) -- see HasSuccessfulConnectorRun's remarks (issue #471).
    private volatile bool _hasSuccessfulConnectorRun;

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
        LspReqnrollProject project, IIdeSupportLogger logger, IFileSystemForIDE? fileSystem = null,
        ILspTelemetryService? telemetryService = null)
        : this(project, CreateDefaultDiscoveryService(logger, fileSystem), logger, telemetryService)
    {
    }

    private static IConnectorDiscoveryService CreateDefaultDiscoveryService(
        IIdeSupportLogger logger, IFileSystemForIDE? fileSystem) =>
        new ConnectorDiscoveryService(logger, new OutProcReqnrollConnectorFactory(logger), fileSystem ?? new FileSystemForIDE());

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

    /// <summary>
    /// True once the out-of-process connector has successfully loaded real bindings from a
    /// compiled DLL at least once (a genuine registry swap in <see cref="RunDiscoveryAsync"/>, not
    /// its hash-match no-op path). Deliberately narrower than "<see cref="Current"/> is populated":
    /// <see cref="ApplyRoslynFileUpdateAsync"/> can populate <see cref="Current"/> too, from a live
    /// Roslyn per-file patch, which is exactly the case (an unbuilt project, no compiled DLL yet)
    /// where the caller of this property still needs to keep relying on that path.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Discovery.CSharpBindingDiscoveryService.UpdateFromSourceAsync"/> to skip
    /// a redundant source-level parse on <c>textDocument/didOpen</c> once the connector has already
    /// covered the project — confirmed live to otherwise re-parse the exact same unedited file
    /// twice within seconds (issue #471). VS Code specifically relies on the un-gated path staying
    /// available before this becomes true: its extension only ever runs a design-time MSBuild
    /// evaluation, never an actual build, so a freshly cloned, not-yet-built project can go its
    /// entire first session with this property false.
    /// </remarks>
    public bool HasSuccessfulConnectorRun => _hasSuccessfulConnectorRun;

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
    /// <param name="file">The file's path and current source text.</param>
    /// <param name="notify">
    /// Whether to raise <see cref="BindingRegistryChanged"/> when the patch actually changes
    /// something. Pass <see langword="false"/> when the caller is itself a sub-step of a larger,
    /// already-coordinated flow that will reparse and notify unconditionally once it's done --
    /// e.g. <c>BindingRegistryChangedHandler.RediscoverCsFilesAsync</c>'s post-connector-run
    /// overlay (issue #471). Reconciling Roslyn-parsed source on top of the connector's
    /// reflection-based extraction of the same, unedited file routinely trips
    /// <see cref="ProjectBindingRegistry.HasExpressionChanges"/> below even with zero real edits
    /// (the two extraction methods aren't guaranteed byte-identical), so notifying there fired a
    /// second, fully independent <c>BindingRegistryChangedNotification</c> that redundantly
    /// reparsed every open feature file a second time -- confirmed live as part of a ~10s pileup
    /// per startup discovery run. Every other caller should keep the default: a live edit (or a
    /// file deletion) is not covered by any other notify path, so it must raise the event itself.
    /// </param>
    /// <remarks>
    /// This is the in-process counterpart to the out-of-process reflection connector: it gives
    /// instant feedback as the user edits a step-definition file, without waiting for a build.
    /// The patch is layered on top of <see cref="Current"/> and intentionally does <b>not</b>
    /// advance the connector's last-good hash, so the next successful connector run (after a real
    /// build, whose assembly hash differs) replaces the whole registry with the authoritative
    /// post-build result. If no build has happened, the connector run is a hash-match no-op and
    /// the Roslyn patch survives.
    /// </remarks>
    public async Task ApplyRoslynFileUpdateAsync(CSharpStepDefinitionFile file, bool notify = true)
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

        if (!notify)
            return;

        // Skip the notification entirely when no binding's matched expression/scope actually
        // changed (e.g. a method-body or comment edit). Publishing here drives feature-file
        // reparsing downstream (BindingRegistryChangedHandler), which can only produce a
        // different result when a step definition's expression or a hook's scope/order was
        // added, removed, or edited -- so there's nothing for that pipeline to do, and running
        // it anyway would just burn CPU on every keystroke. Both checks are needed: a hook-only
        // edit (e.g. adding [BeforeScenario]) doesn't touch any step definition, so relying on
        // HasExpressionChanges alone left the hook-count CodeLens stale until the next full
        // rebuild (issue #372 follow-up).
        if (!ProjectBindingRegistry.HasExpressionChanges(previous, updated, file.FullName)
            && !ProjectBindingRegistry.HasHookChanges(previous, updated, file.FullName))
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

            // Only reachable when RunDiscovery actually found and read a compiled DLL (it returns
            // the unchanged lastHash -- never a genuinely new one -- when OutputAssemblyPath is
            // unset or the file doesn't exist yet, so this branch can't be reached by an unbuilt
            // project). See HasSuccessfulConnectorRun's remarks.
            _hasSuccessfulConnectorRun = true;

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
