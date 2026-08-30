using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Handles <c>workspace/didChangeWatchedFiles</c> notifications.
/// <list type="bullet">
///   <item><term>reqnroll.json changes</term>
///     <description>Reload the owning project's configuration and trigger binding re-discovery.</description>
///   </item>
///   <item><term>.editorconfig changes</term>
///     <description>Invalidate the EditorConfig cache and reload configuration for all projects
///     in the affected workspace scope.</description>
///   </item>
///   <item><term>Output assembly changes (<c>bin/**/*.dll</c>)</term>
///     <description>Trigger binding re-discovery for the project whose output path matches.</description>
///   </item>
///   <item><term>C# source file deletions (<c>*.cs</c>)</term>
///     <description>Remove the deleted file's step definitions from the binding registry so that
///     stale entries do not appear in Find Unused Step Definitions results.</description>
///   </item>
/// </list>
/// </summary>
public class WatchedFilesHandler : IDidChangeWatchedFilesHandler
{
    private readonly ILspWorkspaceScopeManager    _scopeManager;
    private readonly IMediator                    _mediator;
    private readonly IIdeSupportLogger              _logger;
    private readonly IEditorConfigOptionsProvider _editorConfigProvider;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="WatchedFilesHandler"/> class.</summary>
    public WatchedFilesHandler(
        ILspWorkspaceScopeManager scopeManager,
        IMediator mediator,
        IIdeSupportLogger logger,
        IEditorConfigOptionsProvider editorConfigProvider,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        IOperationDurationRecorder? recorder = null)
    {
        _scopeManager           = scopeManager;
        _mediator               = mediator;
        _logger                 = logger;
        _editorConfigProvider   = editorConfigProvider;
        _csharpDiscoveryService = csharpDiscoveryService;
        _recorder               = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Builds the LSP registration options requesting file-watch notifications for <c>reqnroll.json</c>, <c>.editorconfig</c>, and rebuilt output assemblies.</summary>
    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions(
        DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            Watchers = new[]
            {
#pragma warning disable CS8601 // GlobPattern implicit conversion from string returns GlobPattern? but value is provably non-null
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/reqnroll.json",
                    Kind        = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/.editorconfig",
                    Kind        = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                // Broad watcher for rebuilt output assemblies.  Narrowed to the specific
                // OutputAssemblyPath per project once a reqnroll/projectLoaded notification
                // has been received and the path is known.
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = "**/bin/**/*.dll",
                    Kind        = WatchKind.Create | WatchKind.Change
                }
#pragma warning restore CS8601
            }
        };

    /// <summary>Handles <c>workspace/didChangeWatchedFiles</c> for watched <c>reqnroll.json</c>, <c>.editorconfig</c>, and rebuilt-assembly events, invalidating caches and re-running discovery/config reconciliation as appropriate.</summary>
    public async Task<MediatR.Unit> Handle(
        DidChangeWatchedFilesParams request,
        CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.WorkspaceDidChangeWatchedFiles);

        foreach (var fileEvent in request.Changes)
        {
            var uri        = fileEvent.Uri;
            var changeType = fileEvent.Type;
            var filePath   = uri.GetFileSystemPath() ?? string.Empty;

            if (IsReqnrollConfig(filePath))
            {
                await HandleConfigChangeAsync(uri, filePath, changeType, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (IsEditorConfig(filePath))
            {
                await HandleEditorConfigChangeAsync(uri, filePath, changeType, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (IsOutputAssembly(filePath))
            {
                HandleOutputAssemblyChange(filePath, changeType);
            }
            else if (IsCsSource(filePath) && changeType == FileChangeType.Deleted)
            {
                await HandleCsSourceDeletedAsync(uri, filePath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return MediatR.Unit.Value;
    }

    // ── reqnroll.json change ──────────────────────────────────────────────────

    private async Task HandleConfigChangeAsync(
        DocumentUri uri,
        string filePath,
        FileChangeType changeType,
        CancellationToken ct)
    {
        var project = _scopeManager.GetProjectForUri(uri);
        if (project is null)
        {
            _logger.LogVerbose(
                $"reqnroll.json event ({changeType}) for {filePath} — no matching project, skipping.");
            return;
        }

        _logger.LogInfo(
            $"reqnroll.json {changeType}: reloading config for project '{project.ProjectName}'");

        var provider = project.GetDeveroomConfigurationProvider()
            as ProjectScopeDeveroomConfigurationProvider;
        provider?.Reload();

        await _mediator.Publish(
            new ReqnrollConfigChangedNotification(project.ProjectFolder), ct)
            .ConfigureAwait(false);

        // Config change can affect discovery inputs — trigger a re-run.
        TriggerBindingDiscovery(project, "config change");
    }

    // ── .editorconfig change ──────────────────────────────────────────────────

    private async Task HandleEditorConfigChangeAsync(
        DocumentUri uri,
        string filePath,
        FileChangeType changeType,
        CancellationToken ct)
    {
        // Always evict the parse cache so the next EditorConfig lookup re-reads from disk.
        _editorConfigProvider.InvalidateCache(filePath);

        var scope = _scopeManager.GetScopeForUri(uri);
        if (scope is null)
        {
            _logger.LogVerbose(
                $".editorconfig {changeType}: {filePath} — no matching workspace scope; cache invalidated.");
            return;
        }

        _logger.LogInfo(
            $".editorconfig {changeType}: reloading config for {scope.Projects.Count} project(s) in '{scope.RootFolder}'");

        foreach (var project in scope.Projects)
        {
            var provider = project.GetDeveroomConfigurationProvider()
                as ProjectScopeDeveroomConfigurationProvider;
            provider?.Reload();

            await _mediator.Publish(
                new ReqnrollConfigChangedNotification(project.ProjectFolder), ct)
                .ConfigureAwait(false);
        }
    }

    // ── Output assembly change ────────────────────────────────────────────────

    private void HandleOutputAssemblyChange(string filePath, FileChangeType changeType)
    {
        var project = _scopeManager.GetProjectByOutputPath(filePath);
        if (project is null)
        {
            // The changed assembly may not belong to any registered Reqnroll project;
            // this is expected for dependency assemblies.
            _logger.LogVerbose(
                $"Output assembly {changeType}: {filePath} — no matching project output path.");
            return;
        }

        _logger.LogVerbose(
            $"Output assembly {changeType}: triggering discovery for '{project.ProjectName}'");
        TriggerBindingDiscovery(project, "output assembly change");
    }

    // ── C# source deletion ────────────────────────────────────────────────────

    private async Task HandleCsSourceDeletedAsync(
        DocumentUri uri,
        string filePath,
        CancellationToken ct)
    {
        _logger.LogInfo($"C# source deleted: removing bindings for '{filePath}'");
        await _csharpDiscoveryService.RemoveFileAsync(uri, ct).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsReqnrollConfig(string filePath)
        => Path.GetFileName(filePath).Equals(
            "reqnroll.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditorConfig(string filePath)
        => Path.GetFileName(filePath).Equals(
            ".editorconfig", StringComparison.OrdinalIgnoreCase);

    private static bool IsCsSource(string filePath)
        => filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputAssembly(string filePath)
        => filePath.IndexOf(
               Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase) >= 0
           && filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private void TriggerBindingDiscovery(LspReqnrollProject project, string reason)
    {
        if (project.Properties.TryGetValue(
                typeof(ConnectorBindingRegistryProvider), out var obj)
            && obj is ConnectorBindingRegistryProvider provider)
        {
            provider.TriggerRefresh();
        }
        else
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] No ConnectorBindingRegistryProvider in Properties " +
                $"(reason: {reason}); skipping discovery trigger.");
        }
    }
}
