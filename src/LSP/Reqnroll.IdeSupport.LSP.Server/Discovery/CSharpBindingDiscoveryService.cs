using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

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

        foreach (var project in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyToProjectAsync(project, filePath, text).ConfigureAwait(false);
        }

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
