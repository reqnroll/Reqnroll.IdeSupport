using MediatR;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles <see cref="BindingRegistryChangedNotification"/> by re-pushing binding-validation
/// diagnostics (issue #514) for every currently-open <c>.cs</c> file owned by the affected
/// project.
/// </summary>
/// <remarks>
/// <see cref="ICSharpDiagnosticsPublisher"/>'s remarks explain why this is needed alongside the
/// direct push from <see cref="Features.TextSync.TextDocumentSyncHandler"/>'s own doc-sync
/// handlers: a registry change that didn't originate from this file's own <c>didOpen</c>/
/// <c>didChange</c> — most notably the connector's startup reconciliation
/// (<see cref="BindingRegistryChangedHandler.RediscoverCsFilesAsync"/>) racing this file's own
/// <c>didOpen</c> — would otherwise leave its diagnostics stale until the user's next edit.
/// Every open <c>.cs</c> file owned by the project is re-pushed unconditionally on any change
/// (not diffed first) — mirroring <see cref="BindingRegistryChangedHandler.ReparseOpenFilesAsync"/>'s
/// equivalent handling for <c>.feature</c> files, and cheap for the same reason: re-aggregating a
/// handful of already-open files' diagnostics from the in-memory registry is far short of
/// whatever just changed the registry in the first place (a connector run or a Roslyn parse).
/// </remarks>
public sealed class CSharpDiagnosticsRegistryChangedHandler : INotificationHandler<BindingRegistryChangedNotification>
{
    private readonly ICSharpFileTextCache _csharpFileTextCache;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly ICSharpDiagnosticsPublisher _publisher;

    /// <summary>Initializes a new instance of the <see cref="CSharpDiagnosticsRegistryChangedHandler"/> class.</summary>
    public CSharpDiagnosticsRegistryChangedHandler(
        ICSharpFileTextCache csharpFileTextCache,
        ILspWorkspaceScopeManager scopeManager,
        ICSharpDiagnosticsPublisher publisher)
    {
        _csharpFileTextCache = csharpFileTextCache;
        _scopeManager = scopeManager;
        _publisher = publisher;
    }

    /// <summary>Re-pushes diagnostics for every open .cs file owned by the affected project.</summary>
    public Task Handle(BindingRegistryChangedNotification notification, CancellationToken cancellationToken)
    {
        var project = notification.Project;

        foreach (var entry in _csharpFileTextCache.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOwnedByProject(entry.Uri, project))
                _publisher.Publish(entry.Uri, version: null);
        }

        return Task.CompletedTask;
    }

    private bool IsOwnedByProject(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri, LspReqnrollProject project)
    {
        if (_scopeManager.HasBaselineForProject(project))
            return _scopeManager.GetProjectsForUri(uri).Contains(project);

        return PathUtils.IsUnderFolder(uri.GetFileSystemPath(), project.ProjectFolder);
    }
}
