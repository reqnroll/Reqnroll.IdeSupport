using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Thread-safe implementation of <see cref="ILspWorkspaceScopeManager"/>.
/// </summary>
public sealed class LspWorkspaceScopeManager : ILspWorkspaceScopeManager, IDisposable
{
    private readonly IIdeScope _ideScope;
    private readonly IIdeSupportLogger _logger;
    private readonly IMediator _mediator;

    private readonly ConcurrentDictionary<string, LspProjectScope> _scopes
        = new(StringComparer.OrdinalIgnoreCase);

    // The Q17 project-membership index is a genuinely separate concern from workspace/project
    // lifecycle tracking above; see MembershipIndex's own remarks for why it isn't fully
    // independent (it needs FindProjectByKey, which only _scopes above can answer).
    private readonly MembershipIndex _membershipIndex;

    /// <summary>Initializes a new instance of the <see cref="LspWorkspaceScopeManager"/> class.</summary>
    public LspWorkspaceScopeManager(IIdeScope ideScope, IIdeSupportLogger logger, IMediator mediator)
    {
        _ideScope  = ideScope;
        _logger    = logger;
        _mediator  = mediator;
        _membershipIndex = new MembershipIndex(logger, mediator, FindProjectByKey);
    }

    // ── Folder lifecycle ──────────────────────────────────────────────────────

    /// <summary>Raised when a new workspace folder scope is opened.</summary>
    public event Action<LspProjectScope>? ScopeOpened;
    /// <summary>Raised when a workspace folder scope is closed.</summary>
    public event Action<LspProjectScope>? ScopeClosed;

    /// <summary>Creates the workspace scope for <paramref name="rootPath"/> if it does not already exist, raising <see cref="ScopeOpened"/>.</summary>
    public void OpenWorkspace(string rootPath)
    {
        var key = Normalise(rootPath);
        LspProjectScope? added = null;
        _scopes.GetOrAdd(key, k =>
        {
            _logger.LogInfo($"Opening workspace scope: {k}");
            added = new LspProjectScope(k, _ideScope);
            return added;
        });
        if (added is not null)
            ScopeOpened?.Invoke(added);
    }

    /// <summary>Removes the workspace scope for <paramref name="rootPath"/>, raising <see cref="ProjectRemoved"/> for each of its projects and then <see cref="ScopeClosed"/>, and disposes the scope.</summary>
    public void CloseWorkspace(string rootPath)
    {
        var key = Normalise(rootPath);
        if (!_scopes.TryRemove(key, out var scope))
            return;

        _logger.LogInfo($"Closing workspace scope: {key}");

        // Raise ProjectRemoved for every project still inside the scope.
        foreach (var project in scope.Projects)
        {
            ProjectRemoved?.Invoke(project);
        }

        ScopeClosed?.Invoke(scope);
        scope.Dispose();
    }

    // ── Project lifecycle ─────────────────────────────────────────────────────

    /// <summary>Raised when a Reqnroll project is discovered (loaded) in the workspace.</summary>
    public event Action<LspReqnrollProject>? ProjectDiscovered;
    /// <summary>Raised when a Reqnroll project is removed (unloaded) from the workspace.</summary>
    public event Action<LspReqnrollProject>? ProjectRemoved;

    /// <summary>Handles a <c>reqnroll/projectLoaded</c> notification: ensures the workspace folder and project scope exist, updates or creates the project, and raises <see cref="ProjectDiscovered"/>.</summary>
    public Task HandleProjectLoadedAsync(
        ReqnrollProjectLoadedParams parameters,
        CancellationToken cancellationToken)
    {
        // Ensure the workspace folder exists (create it if the IDE sends the project
        // notification before the LSP initialize workspace-folders arrive).
        var folderKey = Normalise(parameters.WorkspaceFolder);
        var scope = _scopes.GetOrAdd(folderKey, k =>
        {
            _logger.LogInfo($"Auto-creating workspace scope for project notification: {k}");
            var newScope = new LspProjectScope(k, _ideScope);
            ScopeOpened?.Invoke(newScope);
            return newScope;
        });

        var (project, isNew, discoveryInputChanged) = scope.AddOrUpdateProject(parameters);

        if (isNew)
        {
            _logger.LogInfo(
                $"Project discovered: {project.ProjectName} " +
                $"[{project.TargetFrameworkMoniker}] → {project.OutputAssemblyPath}");
            // ProjectDiscovered subscribers (BindingRegistryProviderRouter) create the
            // per-project provider and trigger the initial discovery, so no explicit
            // refresh is needed here for a brand-new project.
            ProjectDiscovered?.Invoke(project);
        }
        else
        {
            _logger.LogInfo(
                $"Project updated: {project.ProjectName} " +
                $"[{project.TargetFrameworkMoniker}] → {project.OutputAssemblyPath}");

            // An existing project whose output assembly path or target framework changed
            // (e.g. a rebuild, or a Debug→Release switch that moves the output path) must
            // re-run binding discovery.  The output-assembly file watcher does not reliably
            // cover the path-change case: GetProjectByOutputPath matches on the *old* path
            // until this update lands, so the watcher event for the new DLL can be dropped.
            if (discoveryInputChanged)
                TriggerBindingDiscovery(project);
        }

        // The project's baseline may have already arrived (see HandleProjectFilesAsync) before
        // this registration — that full re-scan was deferred since no project existed yet to
        // attribute it to. Fire it now: any .cs buffers synced during the race window were
        // evaluated with zero known owners and would otherwise never be re-evaluated, silently
        // gating live Roslyn re-discovery for them until the next full build (issue #48).
        if (_membershipIndex.TryConsumePendingFullRescan(project))
        {
            _logger.LogInfo(
                $"[Membership] Firing deferred full re-scan for '{project.ProjectName}' now that the project has loaded.");
            // See BindingRegistryProviderRouter.OnProviderChanged (issue #477): discarding
            // the Publish Task would not actually defer it off this call stack. CancellationToken.None,
            // not the incoming `cancellationToken`: that token is scoped to this notification's
            // own request lifetime, which the background continuation deliberately outlives --
            // forwarding it would let the publish get silently cancelled before it even runs.
            FireAndForgetExtensions.FireAndForget(
                () => _mediator.Publish(new BindingRegistryChangedNotification(project, true), CancellationToken.None),
                _logger, nameof(HandleProjectLoadedAsync));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Triggers a debounced binding re-discovery on the per-project
    /// <see cref="ConnectorBindingRegistryProvider"/> stored in the project's property bag,
    /// if one has been registered by <see cref="BindingRegistryProviderRouter"/>.
    /// </summary>
    private void TriggerBindingDiscovery(LspReqnrollProject project)
    {
        if (project.Properties.TryGetValue(
                typeof(ConnectorBindingRegistryProvider), out var obj)
            && obj is ConnectorBindingRegistryProvider provider)
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] Discovery inputs changed; triggering re-discovery.");
            provider.TriggerRefresh();
        }
        else
        {
            _logger.LogVerbose(
                $"[{project.ProjectName}] Discovery inputs changed but no binding provider " +
                $"registered yet; skipping refresh.");
        }
    }

    /// <summary>Handles a <c>reqnroll/projectUnloaded</c> notification: removes the matching project from its scope, raises <see cref="ProjectRemoved"/>, and disposes it.</summary>
    public Task HandleProjectUnloadedAsync(
        ReqnrollProjectUnloadedParams parameters,
        CancellationToken cancellationToken)
    {
        foreach (var scope in _scopes.Values)
        {
            var removed = scope.RemoveProject(parameters.ProjectFile);
            if (removed is null)
                continue;

            _logger.LogInfo($"Project removed: {removed.ProjectName}");
            ProjectRemoved?.Invoke(removed);
            removed.Dispose();
            return Task.CompletedTask;
        }

        _logger.LogVerbose(
            $"HandleProjectUnloadedAsync: no project found for {parameters.ProjectFile}");
        return Task.CompletedTask;
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>Finds the workspace scope whose root folder most closely contains <paramref name="uri"/>'s file path, if any.</summary>
    public LspProjectScope? GetScopeForUri(DocumentUri uri)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return null;

        return _scopes.Values
            .Where(s => PathUtils.IsUnderFolder(filePath, s.RootFolder))
            .OrderByDescending(s => s.RootFolder.Length)
            .FirstOrDefault();
    }

    /// <summary>Finds the single project whose folder most closely contains <paramref name="uri"/>'s file path, without consulting the membership index (see <see cref="ResolveOwners"/> for index-aware ownership).</summary>
    public LspReqnrollProject? GetProjectForUri(DocumentUri uri)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return null;

        return _scopes.Values
            .SelectMany(s => s.Projects)
            .Where(p => PathUtils.IsUnderFolder(filePath, p.ProjectFolder))
            .OrderByDescending(p => p.ProjectFolder.Length)
            .FirstOrDefault();
    }

    /// <summary>Finds the project whose compiled output assembly path matches <paramref name="assemblyPath"/>, if any.</summary>
    public LspReqnrollProject? GetProjectByOutputPath(string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath))
            return null;

        return _scopes.Values
            .SelectMany(s => s.Projects)
            .FirstOrDefault(p => string.Equals(
                p.OutputAssemblyPath, assemblyPath,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the owning project's configuration provider for <paramref name="uri"/>, or a default configuration provider when no project covers it.</summary>
    public IIdeSupportConfigurationProvider GetConfigurationProviderForUri(DocumentUri uri)
    {
        var project = GetProjectForUri(uri);
        if (project is not null)
            return project.GetIdeSupportConfigurationProvider();

        // Fallback: default configuration when no project covers the URI.
        return new ProjectSystemIdeSupportConfigurationProvider(_ideScope);
    }

    // ── Membership index (workspace scope / project membership tracking) ────
    // Delegates to MembershipIndex (see its own remarks) for everything that doesn't also need
    // _scopes-based folder-prefix fallback logic.

    /// <summary>Handles a <c>reqnroll/projectFiles</c> notification, applying it as a full baseline replacement or an incremental delta to the membership index.</summary>
    public Task HandleProjectFilesAsync(
        ReqnrollProjectFilesParams parameters,
        CancellationToken cancellationToken)
        => _membershipIndex.HandleProjectFilesAsync(parameters, cancellationToken);

    /// <summary>Looks up every project that claims <paramref name="uri"/> via the membership index (does not fall back to folder-prefix matching).</summary>
    public IReadOnlyCollection<LspReqnrollProject> GetProjectsForUri(DocumentUri uri)
        => _membershipIndex.GetProjectsForUri(uri);

    /// <summary>Resolves the projects that own <paramref name="uri"/>, preferring the membership index and falling back to folder-prefix matching only while the covering project's baseline is still pending.</summary>
    public IReadOnlyCollection<LspReqnrollProject> ResolveOwners(DocumentUri uri)
    {
        var indexOwners = GetProjectsForUri(uri);
        if (indexOwners.Count > 0)
            return indexOwners;

        // Fall back to folder-prefix for files whose covering project hasn't sent a baseline.
        if (GetMembershipState(uri) == MembershipState.Pending)
        {
            var fallback = GetProjectForUri(uri);
            return fallback is not null ? [fallback] : [];
        }

        return [];  // Unowned
    }

    /// <summary>Picks a single "primary" owning project for <paramref name="uri"/> when multiple projects claim it, preferring the project whose folder contains the file (longest match), else falling back to an ordinal tiebreak for stability.</summary>
    public LspReqnrollProject? ResolvePrimaryOwner(DocumentUri uri)
    {
        var owners = ResolveOwners(uri);
        if (owners.Count == 0)
            return null;
        if (owners.Count == 1)
            return owners.First();

        var filePath = uri.GetFileSystemPath() ?? string.Empty;

        // Prefer the owner whose ProjectFolder is a prefix of the file path (home project).
        // If several qualify, pick the longest prefix (most specific containing project).
        var homeOwners = owners
            .Where(p => PathUtils.IsUnderFolder(filePath, p.ProjectFolder))
            .OrderByDescending(p => p.ProjectFolder.Length)
            .ToList();

        if (homeOwners.Count > 0)
            return homeOwners[0];

        // File is outside every owner's folder (genuinely external/linked). Use ordinal tiebreak
        // on ProjectFullName so the result is stable regardless of baseline-arrival order.
        return owners
            .OrderBy(p => p.ProjectFullName, StringComparer.Ordinal)
            .First();
    }

    /// <summary>Classifies whether <paramref name="uri"/> is <see cref="MembershipState.Owned"/> in the index, still <see cref="MembershipState.Pending"/> a covering project's baseline, or <see cref="MembershipState.Unowned"/>.</summary>
    public MembershipState GetMembershipState(DocumentUri uri)
    {
        // Normalised once here (not just inside IsPathOwned) because filePath is also used
        // below for the PathUtils.IsUnderFolder folder-prefix checks, which do no
        // normalisation of their own -- unlike the membership-index lookup, which normalises
        // internally regardless.
        var filePath = MembershipIndex.NormaliseFilePath(uri.GetFileSystemPath() ?? string.Empty);
        if (string.IsNullOrEmpty(filePath))
            return MembershipState.Unowned;

        if (_membershipIndex.IsPathOwned(filePath))
            return MembershipState.Owned;

        // Any project that would cover this path via folder-prefix?
        var covering = _scopes.Values
            .SelectMany(s => s.Projects)
            .Where(p => PathUtils.IsUnderFolder(filePath, p.ProjectFolder))
            .ToList();

        if (covering.Count == 0)
        {
            // No *registered* project covers this path yet. That is not the same as the
            // path being permanently excluded: at startup, a workspace folder can be open
            // (or about to open) well before its `reqnroll/projectLoaded` notification
            // arrives, and file sync (didOpen/didChange) can race ahead of it. As long as
            // the path falls inside a known workspace-folder scope, a covering project may
            // still register momentarily, so treat this as Pending rather than a definitive
            // Unowned — Unowned must only fire once we can be sure nothing will ever claim
            // the file (see invariant I2 in CSharpBindingDiscoveryService).
            var insideKnownScope = _scopes.Values.Any(
                s => PathUtils.IsUnderFolder(filePath, s.RootFolder));

            return insideKnownScope ? MembershipState.Pending : MembershipState.Unowned;
        }

        // Pending if any covering project has not yet sent a baseline.
        foreach (var project in covering)
        {
            if (!_membershipIndex.HasBaselineForProject(project))
                return MembershipState.Pending;
        }

        return MembershipState.Unowned;
    }

    /// <summary>Returns every file path in the membership index that <paramref name="project"/> owns with the <see cref="ProjectFileRole.Feature"/> role.</summary>
    public IReadOnlyCollection<string> GetIndexedFeatureFiles(LspReqnrollProject project)
        => _membershipIndex.GetIndexedFeatureFiles(project);

    /// <summary>Returns every file path in the membership index that <paramref name="project"/> owns with the <see cref="ProjectFileRole.Binding"/> role.</summary>
    public IReadOnlyCollection<string> GetBindingFilePathsForProject(LspReqnrollProject project)
        => _membershipIndex.GetBindingFilePathsForProject(project);

    /// <summary>Returns whether <paramref name="project"/> has received its initial full membership baseline yet.</summary>
    public bool HasBaselineForProject(LspReqnrollProject project)
        => _membershipIndex.HasBaselineForProject(project);

    private LspReqnrollProject? FindProjectByKey(ProjectKey key)
    {
        // Phase 1: match by ProjectFile only (TFM keying is a planned follow-up).
        return _scopes.Values
            .SelectMany(s => s.Projects)
            .FirstOrDefault(p => string.Equals(
                MembershipIndex.NormaliseFilePath(p.ProjectFullName),
                key.ProjectFile,
                StringComparison.OrdinalIgnoreCase));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Closes every open workspace scope, disposing each one and raising <see cref="ProjectRemoved"/>/<see cref="ScopeClosed"/> as needed.</summary>
    public void Dispose()
    {
        foreach (var key in _scopes.Keys.ToArray())
            CloseWorkspace(key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalise(string path)
        => Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
}
