using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;

/// <summary>
/// Drives Roslyn/C# source-level binding discovery when a
/// <c>.cs</c> step-definition file is opened or edited.  Resolves all projects that own the
/// document via the membership index (<see cref="ILspWorkspaceScopeManager.ResolveOwners"/>),
/// parses the supplied source text with <see cref="StepDefinitionFileParser"/> (via
/// <see cref="ProjectBindingRegistry.ReplaceBindings"/>), and patches each owning project's
/// <see cref="ConnectorBindingRegistryProvider"/> so the change is reflected immediately
/// without waiting for a build / connector run.
/// </summary>
/// <remarks>
/// Invariant I2 — if the membership index has received a baseline and the file is not in it,
/// the file is <em>excluded</em> from all registries and this method is a no-op for it.
/// This prevents phantom bindings from open-but-excluded files.
/// </remarks>
public sealed class CSharpBindingDiscoveryService : ICSharpBindingDiscoveryService
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="CSharpBindingDiscoveryService"/> class.</summary>
    public CSharpBindingDiscoveryService(
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService = null)
    {
        _scopeManager = scopeManager;
        _logger = logger;
        _telemetryService = telemetryService;
    }

    /// <summary>Resolves the project(s) that own <paramref name="uri"/> via the membership index, re-parses the given source text into each project's binding registry, and emits a discovery telemetry event.</summary>
    public async Task UpdateFromSourceAsync(DocumentUri uri, string text, bool isOpen, CancellationToken cancellationToken)
    {
        var owners = _scopeManager.ResolveOwners(uri);

        if (owners.Count == 0)
        {
            var state = _scopeManager.GetMembershipState(uri);
            if (state == MembershipState.Unowned)
                _logger.LogVerbose(
                    $"[Roslyn] '{uri}' is excluded from all projects (I2); skipping source-level discovery.");
            else
                _logger.LogVerbose(
                    $"[Roslyn] No project owns '{uri}' (state={state}); skipping source-level discovery.");
            return;
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var appliedToAnyProject = false;
        foreach (var project in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // On a mere didOpen (not an edit), skip the parse+patch once the project's connector
            // has already succeeded at least once *and* the registry already has bindings for this
            // exact file: BindingRegistryChangedHandler.RediscoverCsFilesAsync already covers this
            // exact file (with this exact buffer text, via ICSharpFileTextCache) as part of that
            // reconciliation, so reparsing here repeats that work for zero new information --
            // confirmed live, the same file parsed twice within 7 seconds with nothing having
            // changed in between. HasSuccessfulConnectorRun alone only proves a connector run
            // happened for the *project*, not that *this file* was actually covered by that
            // reconciliation pass -- a didOpen arriving after RediscoverCsFilesAsync took its
            // snapshot (e.g. while the IDE is still restoring tabs) would otherwise never get
            // Roslyn-parsed until the user's first edit (issue #517). Checking HasAnyBindingFor
            // closes that gap while still skipping the redundant parse for a file that genuinely
            // was already reconciled.
            if (isOpen && HasSuccessfulConnectorRun(project, out var provider) &&
                provider!.Current.HasAnyBindingFor(filePath))
            {
                _logger.LogVerbose(
                    $"[Roslyn] '{uri}' opened but '{project.ProjectName}' already has a successful " +
                    "connector run with bindings for this file; skipping redundant source-level parse.");
                continue;
            }

            await ApplyToProjectAsync(project, filePath, text).ConfigureAwait(false);
            appliedToAnyProject = true;
        }

        if (!appliedToAnyProject)
            return;

        // Telemetry: Roslyn discovery event (membership index / telemetry design §2.3).
        var fileName = Path.GetFileName(filePath);
        var triggerContext = isOpen ? "csOpen" : "csEdit";
        _telemetryService?.SendEvent("Reqnroll Discovery executed", new()
        {
            ["DiscoverySource"] = "Roslyn",
            ["TriggerContext"] = triggerContext,
            ["IsFailed"] = false,
            ["AffectedFile"] = fileName,
            ["ProjectCount"] = owners.Count,
            ["ProjectTargetFramework"] = owners.FirstOrDefault()?.TargetFrameworkMonikers,
        });
    }

    /// <summary>Re-parses <paramref name="text"/> directly into <paramref name="project"/>'s binding registry, bypassing membership-index owner resolution.</summary>
    public async Task UpdateFromSourceForProjectAsync(
        LspReqnrollProject project, string filePath, string text, CancellationToken cancellationToken,
        bool notify = true)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        await ApplyToProjectAsync(project, filePath, text, notify).ConfigureAwait(false);
    }

    /// <summary>Clears all step-definition bindings previously discovered for <paramref name="uri"/> from every owning project's registry, e.g. when the file is deleted.</summary>
    public async Task RemoveFileAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
        {
            _logger.LogVerbose($"[Roslyn] No project owns '{uri}' for deletion; no binding removal needed.");
            return;
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var project in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyToProjectAsync(project, filePath, string.Empty).ConfigureAwait(false);
        }
    }

    /// <summary>True when <paramref name="project"/> has a connector provider that has already completed a successful discovery run. See <see cref="ConnectorBindingRegistryProvider.HasSuccessfulConnectorRun"/>'s remarks (issue #471). When true, <paramref name="provider"/> is the provider found, so callers can inspect its current registry (issue #517).</summary>
    private static bool HasSuccessfulConnectorRun(LspReqnrollProject project, out ConnectorBindingRegistryProvider? provider)
    {
        if (project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
            && obj is ConnectorBindingRegistryProvider candidate
            && candidate.HasSuccessfulConnectorRun)
        {
            provider = candidate;
            return true;
        }

        provider = null;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="text"/> and replaces <paramref name="filePath"/>'s entries in
    /// <paramref name="project"/>'s binding registry. Shared by the index-driven
    /// (<see cref="UpdateFromSourceAsync"/>) and index-bypassing
    /// (<see cref="UpdateFromSourceForProjectAsync"/>) entry points.
    /// </summary>
    private async Task ApplyToProjectAsync(LspReqnrollProject project, string filePath, string text, bool notify = true)
    {
        if (!project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
            || obj is not ConnectorBindingRegistryProvider provider)
        {
            _logger.LogVerbose(
                $"[Roslyn] Project '{project.ProjectName}' has no binding provider yet; skipping.");
            return;
        }

        var previousCount = provider.Current.StepDefinitions.Length;
        var file = FileDetails.FromPath(filePath).WithCSharpContent(text);
        await provider.ApplyRoslynFileUpdateAsync(file, notify).ConfigureAwait(false);
        var newCount = provider.Current.StepDefinitions.Length;
        var delta = newCount - previousCount;
        var deltaStr = delta == 0 ? "no change" : (delta > 0 ? $"+{delta}" : delta.ToString());

        _logger.LogInfo(
            $"[Roslyn] Re-discovered bindings for '{Path.GetFileName(filePath)}' " +
            $"in project '{project.ProjectName}': {newCount} step definition(s) ({deltaStr}).");
    }
}
