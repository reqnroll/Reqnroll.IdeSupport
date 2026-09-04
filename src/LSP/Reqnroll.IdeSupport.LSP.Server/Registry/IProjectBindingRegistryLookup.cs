using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Server.Registry;

/// <summary>
/// Resolves the <see cref="ProjectBindingRegistry"/> for a specific document URI within
/// the LSP server, routing the lookup to the <see cref="ConnectorBindingRegistryProvider"/>
/// that owns the project the document belongs to.
/// </summary>
/// <remarks>
/// This is the correct server-side abstraction for step-definition lookup.
/// Unlike <see cref="IBindingRegistryProvider"/> (which is a per-project interface),
/// this interface is URI-aware so consumers do not need to know which project a
/// document belongs to — that routing is done here via
/// <see cref="Workspace.ILspWorkspaceScopeManager.GetProjectForUri"/>.
/// <para>
/// Registry-change notifications flow through MediatR as
/// <see cref="Pipeline.BindingRegistryReplacedNotification"/> /
/// <see cref="Pipeline.BindingRegistryPatchedNotification"/> (issue #577) rather than through a
/// C# event on this interface, so that all cross-cutting LSP concerns follow the same
/// established notification pattern.
/// </para>
/// </remarks>
public interface IProjectBindingRegistryLookup
{
    /// <summary>
    /// Returns the binding registry for the project that owns <paramref name="uri"/>,
    /// or <see cref="ProjectBindingRegistry.Invalid"/> when the document has no
    /// associated project or the project has not yet completed a discovery run.
    /// </summary>
    ProjectBindingRegistry GetRegistryForUri(DocumentUri uri);

    /// <summary>
    /// Returns <see langword="true"/> when any registry owned by the projects that contain
    /// <paramref name="csUri"/> has a step-definition binding whose source span covers
    /// <paramref name="query"/>. Used by <see cref="Handlers.ProtocolHandlers.ReferencesHandler"/>
    /// to distinguish "no binding at this location" (return <see langword="null"/>) from
    /// "binding with zero matching steps" (return empty).
    /// </summary>
    bool HasBindingAtLocation(DocumentUri csUri, SourceLocation query);

    /// <summary>
    /// Returns a snapshot of all currently-known (project name, owner, registry) triplets,
    /// one per discovered project. Used by Find Unused Step Definitions to enumerate all step
    /// definitions workspace-wide.
    /// </summary>
    IReadOnlyList<(string ProjectName, ProjectOwner Owner, ProjectBindingRegistry Registry)> GetAllRegistries();
}
