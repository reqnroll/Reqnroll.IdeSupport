using Reqnroll.IdeSupport.Common;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Represents one LSP workspace folder (the root directory sent by the client in
/// the <c>initialize</c> handshake or via <c>workspace/didChangeWorkspaceFolders</c>).
/// </summary>
/// <remarks>
/// This is a <em>folder container</em>, not a project.  Individual Reqnroll projects
/// (<c>.csproj</c> files) within this folder are tracked as <see cref="LspReqnrollProject"/>
/// instances populated by <c>reqnroll/projectLoaded</c> notifications from the IDE glue.
/// </remarks>
public sealed class LspProjectScope : IDisposable
{
    private readonly IIdeScope _ideScope;
    private readonly ConcurrentDictionary<string, LspReqnrollProject> _projects
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a scope for the given workspace folder root.</summary>
    public LspProjectScope(string rootFolder, IIdeScope ideScope)
    {
        _ideScope   = ideScope;
        RootFolder  = Path.GetFullPath(rootFolder);
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Normalised absolute path of the workspace folder root.</summary>
    public string RootFolder { get; }

    /// <summary>All Reqnroll projects currently registered within this workspace folder.</summary>
    public IReadOnlyCollection<LspReqnrollProject> Projects => _projects.Values.ToArray();

    // ── Project lifecycle (called by LspWorkspaceScopeManager) ────────────────

    /// <summary>
    /// Creates a new <see cref="LspReqnrollProject"/> for the given notification, or
    /// updates the matching existing one in-place and returns it.
    /// </summary>
    /// <returns>
    /// (<c>project</c>, <c>isNew</c>, <c>discoveryInputChanged</c>).
    /// <c>discoveryInputChanged</c> is <see langword="true"/> for a newly created project, or
    /// for an updated project whose output assembly path or target framework moniker changed.
    /// </returns>
    internal (LspReqnrollProject Project, bool IsNew, bool DiscoveryInputChanged) AddOrUpdateProject(
        ReqnrollProjectLoadedParams info)
    {
        var key = NormaliseKey(info.ProjectFile);

        // Use GetOrAdd to prevent the non-atomic check-then-create race where two concurrent
        // calls both see TryGetValue return false, construct separate LspReqnrollProject
        // instances, and the losing one's ConnectorBindingRegistryProvider (with its
        // CancellationTokenSource and background Task.Run) is never disposed/cancelled.
        LspReqnrollProject? created = null;
        var project = _projects.GetOrAdd(key, _ =>
        {
            created = new LspReqnrollProject(info, _ideScope);
            return created;
        });

        if (created == null)
        {
            var changed = project.Update(info);
            return (project, false, changed);
        }

        return (project, true, true);
    }

    /// <summary>
    /// Removes the project identified by <paramref name="projectFile"/> and returns it,
    /// or <c>null</c> if it was not registered.
    /// </summary>
    internal LspReqnrollProject? RemoveProject(string projectFile)
    {
        var key = NormaliseKey(projectFile);
        return _projects.TryRemove(key, out var project) ? project : null;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Disposes all projects registered within this workspace folder and clears the collection.</summary>
    public void Dispose()
    {
        foreach (var project in _projects.Values)
            project.Dispose();
        _projects.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormaliseKey(string path) => Path.GetFullPath(path);

    /// <summary>Returns the workspace folder's root path, for logging and debugging.</summary>
    public override string ToString() => RootFolder;
}
