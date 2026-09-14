using MediatR;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles all three binding-registry-change events (issue #577) by re-pushing binding-validation
/// diagnostics (issue #514) for every currently-open <c>.cs</c> file owned by the affected
/// project. This is the sole trigger for <c>.cs</c> diagnostics — see
/// <see cref="ICSharpDiagnosticsPublisher"/>'s remarks for the two cases folded into this one
/// republish: a live doc-sync edit (including one that changes only a binding's validity, not
/// its expression or scope/order) and a registry change that didn't originate from this file's
/// own doc-sync events at all (e.g. the connector's startup reconciliation
/// (<see cref="BindingRegistryChangedHandler.RediscoverCsFilesAsync"/>) racing this file's own
/// <c>didOpen</c>).
/// </summary>
/// <remarks>
/// Every open <c>.cs</c> file owned by the project is re-pushed unconditionally on any of the
/// three events (not diffed first) — mirroring <see cref="BindingRegistryChangedHandler.ReparseOpenFilesAsync"/>'s
/// equivalent handling for <c>.feature</c> files, and cheap for the same reason: re-aggregating a
/// handful of already-open files' diagnostics from the in-memory registry is far short of
/// whatever just changed the registry in the first place (a connector run, a Roslyn parse, or a
/// membership-index update).
/// <para>
/// Deliberately undiscriminating across the three event types rather than implementing each
/// separately: this handler wants "anything about the registry changed", not any of the finer
/// distinctions (replaced vs. patched vs. files removed) the split exists to expose to other
/// consumers. One consequence worth being explicit about: a membership delta that both removes
/// binding files and patches the registry now publishes <see cref="ProjectBindingFilesRemovedNotification"/>
/// and <see cref="BindingRegistryPatchedNotification"/> as two separate notifications (see
/// <see cref="Workspace.MembershipIndex"/>'s remarks) where the pre-split single notification
/// carried both facts at once — this handler is idempotent (issue #578) by construction, so
/// running twice for what was previously one event is wasted work, not a correctness risk.
/// </para>
/// </remarks>
public sealed class CSharpDiagnosticsRegistryChangedHandler :
    INotificationHandler<BindingRegistryReplacedNotification>,
    INotificationHandler<BindingRegistryPatchedNotification>,
    INotificationHandler<ProjectBindingFilesRemovedNotification>
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
    public Task Handle(BindingRegistryReplacedNotification notification, CancellationToken cancellationToken)
        => RepublishForProjectAsync(notification.Project, cancellationToken);

    /// <summary>Re-pushes diagnostics for every open .cs file owned by the affected project.</summary>
    public Task Handle(BindingRegistryPatchedNotification notification, CancellationToken cancellationToken)
        => RepublishForProjectAsync(notification.Project, cancellationToken);

    /// <summary>Re-pushes diagnostics for every open .cs file owned by the affected project.</summary>
    public Task Handle(ProjectBindingFilesRemovedNotification notification, CancellationToken cancellationToken)
        => RepublishForProjectAsync(notification.Project, cancellationToken);

    private Task RepublishForProjectAsync(LspReqnrollProject project, CancellationToken cancellationToken)
    {
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
