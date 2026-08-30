using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Manages the two-tier workspace model:
/// <list type="bullet">
///   <item><term>Folder tier</term><description>
///     One <see cref="LspProjectScope"/> per LSP workspace folder, created from the
///     <c>initialize</c> handshake or <c>workspace/didChangeWorkspaceFolders</c>.
///   </description></item>
///   <item><term>Project tier</term><description>
///     One <see cref="LspReqnrollProject"/> per <c>.csproj</c> inside a folder,
///     created from <c>reqnroll/projectLoaded</c> notifications sent by IDE glue.
///   </description></item>
/// </list>
/// </summary>
public interface ILspWorkspaceScopeManager
{
    // ── Folder lifecycle ──────────────────────────────────────────────────────

    /// <summary>Raised after a new <see cref="LspProjectScope"/> is registered.</summary>
    event Action<LspProjectScope> ScopeOpened;

    /// <summary>Raised just before a <see cref="LspProjectScope"/> is disposed.</summary>
    event Action<LspProjectScope> ScopeClosed;

    /// <summary>Registers a new workspace-folder root.</summary>
    void OpenWorkspace(string rootPath);

    /// <summary>Disposes and removes the scope for <paramref name="rootPath"/>.</summary>
    void CloseWorkspace(string rootPath);

    // ── Project lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Raised after a new <see cref="LspReqnrollProject"/> is created inside a scope.
    /// Not raised when an existing project's properties are updated.
    /// </summary>
    event Action<LspReqnrollProject> ProjectDiscovered;

    /// <summary>
    /// Raised just before a <see cref="LspReqnrollProject"/> is disposed and removed.
    /// </summary>
    event Action<LspReqnrollProject> ProjectRemoved;

    /// <summary>
    /// Handles a <c>reqnroll/projectLoaded</c> notification.
    /// Creates the containing <see cref="LspProjectScope"/> if it does not yet exist,
    /// then creates or updates the <see cref="LspReqnrollProject"/>.
    /// </summary>
    Task HandleProjectLoadedAsync(ReqnrollProjectLoadedParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles a <c>reqnroll/projectUnloaded</c> notification.
    /// Removes and disposes the matching <see cref="LspReqnrollProject"/>.
    /// </summary>
    Task HandleProjectUnloadedAsync(ReqnrollProjectUnloadedParams parameters,
        CancellationToken cancellationToken);

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="LspProjectScope"/> whose root is the closest ancestor
    /// of the local file path represented by <paramref name="uri"/>, or <c>null</c>.
    /// </summary>
    LspProjectScope? GetScopeForUri(DocumentUri uri);

    /// <summary>
    /// Returns the <see cref="LspReqnrollProject"/> whose project folder is the
    /// closest ancestor of <paramref name="uri"/>, or <c>null</c>.
    /// </summary>
    LspReqnrollProject? GetProjectForUri(DocumentUri uri);

    /// <summary>
    /// Returns the <see cref="LspReqnrollProject"/> whose
    /// <see cref="LspReqnrollProject.OutputAssemblyPath"/> matches
    /// <paramref name="assemblyPath"/> (case-insensitive), or <c>null</c>.
    /// </summary>
    LspReqnrollProject? GetProjectByOutputPath(string assemblyPath);

    /// <summary>
    /// Returns the <see cref="IIdeSupportConfigurationProvider"/> for the project that
    /// covers <paramref name="uri"/>, falling back to a default provider when no
    /// project or workspace matches.
    /// </summary>
    IIdeSupportConfigurationProvider GetConfigurationProviderForUri(DocumentUri uri);

    // ── Primary-owner resolution / shared-feature scoping (phase 2A) ─────────

    /// <summary>
    /// Returns the single deterministic owner for <paramref name="uri"/>:
    /// the owner whose <see cref="LspReqnrollProject.ProjectFolder"/> is the longest path-prefix
    /// of the file (home project); for files outside every owner's folder, the owner with the
    /// ordinally-smallest <see cref="LspReqnrollProject.ProjectFullName"/> is used as a stable
    /// tiebreak. Returns <see langword="null"/> when <see cref="ResolveOwners"/> returns no owners.
    /// </summary>
    LspReqnrollProject? ResolvePrimaryOwner(DocumentUri uri);

    // ── Membership index (workspace scope / project membership tracking) ────

    /// <summary>
    /// Handles a <c>reqnroll/projectFiles</c> notification.
    /// Applies a baseline or delta to the authoritative path → projects membership index.
    /// </summary>
    Task HandleProjectFilesAsync(ReqnrollProjectFilesParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all projects that the membership index attributes <paramref name="uri"/> to.
    /// Returns an empty collection when no project has claimed the file.
    /// </summary>
    IReadOnlyCollection<LspReqnrollProject> GetProjectsForUri(DocumentUri uri);

    /// <summary>
    /// Resolves the owning projects for <paramref name="uri"/> using the full fallback chain:
    /// index hit → owners; pending (no baseline yet) → folder-prefix singleton;
    /// unowned → empty.
    /// </summary>
    IReadOnlyCollection<LspReqnrollProject> ResolveOwners(DocumentUri uri);

    /// <summary>
    /// Returns the membership state of <paramref name="uri"/> relative to the index.
    /// </summary>
    MembershipState GetMembershipState(DocumentUri uri);

    /// <summary>
    /// Returns all feature-file paths attributed to <paramref name="project"/> by the index.
    /// Returns an empty collection when no baseline has been received yet (caller should
    /// check <see cref="HasBaselineForProject"/> to distinguish empty-index from no-index).
    /// </summary>
    IReadOnlyCollection<string> GetIndexedFeatureFiles(LspReqnrollProject project);

    /// <summary>
    /// Returns all binding (C#) file paths attributed to <paramref name="project"/> by the
    /// membership index.  Returns an empty collection when no baseline has been received yet.
    /// </summary>
    IReadOnlyCollection<string> GetBindingFilePathsForProject(LspReqnrollProject project);

    /// <summary>
    /// Returns <see langword="true"/> when a baseline has been received for
    /// <paramref name="project"/>, even if that baseline contained no files.
    /// </summary>
    bool HasBaselineForProject(LspReqnrollProject project);
}
