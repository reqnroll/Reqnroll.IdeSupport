using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// The Q17 project-membership index (path → {<see cref="ProjectKey"/> → <see cref="ProjectFileRole"/>})
/// plus baseline/delta tracking, extracted from <see cref="LspWorkspaceScopeManager"/>. This is a
/// genuinely separate concern from workspace/project lifecycle tracking — the only thing the two
/// share is the ability to resolve a <see cref="ProjectKey"/> back to a live
/// <see cref="LspReqnrollProject"/>, which only the lifecycle side (its <c>_scopes</c> registry)
/// can actually do; that capability is injected as <c>findProjectByKey</c> rather than this class
/// reaching into workspace/project lifecycle state directly.
/// </summary>
internal sealed class MembershipIndex
{
    private readonly IIdeSupportLogger _logger;
    private readonly IMediator _mediator;
    private readonly Func<ProjectKey, LspReqnrollProject?> _findProjectByKey;

    // path (normalised, OrdinalIgnoreCase) → { ProjectKey → ProjectFileRole }
    private readonly Dictionary<string, Dictionary<ProjectKey, ProjectFileRole>> _membership
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _membershipLock = new();
    // project key → baseline-received flag
    private readonly ConcurrentDictionary<ProjectKey, bool> _baselineReceived = new();
    // project keys whose baseline arrived before the project itself registered (see
    // HandleProjectFilesAsync / LspWorkspaceScopeManager.HandleProjectLoadedAsync) — the full
    // re-scan that baseline would normally trigger is deferred here until the project loads.
    private readonly ConcurrentDictionary<ProjectKey, bool> _pendingFullRescan = new();

    public MembershipIndex(
        IIdeSupportLogger logger, IMediator mediator, Func<ProjectKey, LspReqnrollProject?> findProjectByKey)
    {
        _logger = logger;
        _mediator = mediator;
        _findProjectByKey = findProjectByKey;
    }

    /// <summary>Handles a <c>reqnroll/projectFiles</c> notification, applying it as a full baseline replacement or an incremental delta to the membership index.</summary>
    public async Task HandleProjectFilesAsync(
        ReqnrollProjectFilesParams parameters,
        CancellationToken cancellationToken)
    {
        var key = MakeKey(parameters.ProjectFile, parameters.TargetFrameworkMoniker);

        if (parameters.Kind == ProjectFilesKind.Delta)
        {
            if (!_baselineReceived.ContainsKey(key))
            {
                _logger.LogVerbose(
                    $"[Membership] Dropping delta for '{parameters.ProjectFile}': " +
                    "no baseline received yet.");
                return;
            }
            ApplyDelta(key, parameters.Files);

            _logger.LogInfo(
                $"[Membership] Applied delta for '{parameters.ProjectFile}' " +
                $"[{parameters.TargetFrameworkMoniker}]: {parameters.Files.Length} change(s).");

            // A binding-role file removed from the project (e.g. the user deletes a .cs
            // step-definition file) must also have its stale entries purged from the project's
            // binding registry -- otherwise the step keeps showing as bound until the next full
            // build (issue #94). The membership index alone doesn't drive matching; the registry
            // does, so BindingRegistryChangedHandler is handed the removed paths to reconcile.
            var removedBindingPaths = parameters.Files
                .Where(entry => !entry.Added && entry.Role == ProjectFileRole.Binding)
                .Select(entry => entry.Path)
                .ToList();

            // The delta may re-attribute a file that's already open (e.g. Solution Explorer
            // rename: the client's didClose/didOpen for the new URI typically reaches the server
            // before this delta does, so its first parse/diagnostics pass ran with zero owners).
            // Without this, that already-open buffer would show stale/empty diagnostics until
            // the next full build or solution reload (issue #32).
            var deltaProject = _findProjectByKey(key);
            if (deltaProject is not null)
            {
                // See BindingRegistryProviderRouter.OnProviderChanged (issue #477): discarding
                // the Publish Task would not actually defer it off this call stack.
                // CancellationToken.None, not the incoming `cancellationToken`: that token is
                // scoped to this notification's own request lifetime, which FireAndForget's
                // background continuation deliberately outlives. Forwarding it would let the
                // publish get silently cancelled the moment the request completes -- before the
                // backgrounded work even runs -- defeating the notification entirely.
                FireAndForgetExtensions.FireAndForget(
                    () => _mediator.Publish(
                        new BindingRegistryChangedNotification(deltaProject, false, removedBindingPaths),
                        CancellationToken.None),
                    _logger, nameof(HandleProjectFilesAsync));
            }
            return;
        }

        // Baseline: replace this project's contribution wholesale.
        lock (_membershipLock)
        {
            // Remove the project from every path it previously claimed.
            foreach (var path in _membership.Keys.ToList())
            {
                var owners = _membership[path];
                if (owners.Remove(key) && owners.Count == 0)
                    _membership.Remove(path);
            }

            // Add all incoming paths.
            foreach (var entry in parameters.Files)
            {
                var normPath = NormaliseFilePath(entry.Path);
                if (!_membership.TryGetValue(normPath, out var owners))
                {
                    owners = new Dictionary<ProjectKey, ProjectFileRole>();
                    _membership[normPath] = owners;
                }
                owners[key] = entry.Role;
            }
        }

        _baselineReceived[key] = true;

        _logger.LogInfo(
            $"[Membership] Baseline received for '{parameters.ProjectFile}' " +
            $"[{parameters.TargetFrameworkMoniker}]: {parameters.Files.Length} file(s).");

        // Trigger a full re-scan for the project so diagnostics reflect the new index.
        var project = _findProjectByKey(key);
        if (project is not null)
        {
            // See BindingRegistryProviderRouter.OnProviderChanged (issue #477): discarding
            // the Publish Task would not actually defer it off this call stack.
            // CancellationToken.None -- see the delta branch above for why.
            FireAndForgetExtensions.FireAndForget(
                () => _mediator.Publish(new BindingRegistryChangedNotification(project, true), CancellationToken.None),
                _logger, nameof(HandleProjectFilesAsync));
        }
        else
        {
            // The baseline raced ahead of `reqnroll/projectLoaded` (see issue #48): any .cs
            // buffers already synced for this project were evaluated against zero known
            // owners and, absent this flag, would never be re-evaluated once the project
            // actually registers. LspWorkspaceScopeManager checks this flag (via
            // TryConsumePendingFullRescan) and fires the deferred re-scan itself.
            _pendingFullRescan[key] = true;
            _logger.LogVerbose(
                $"[Membership] No live project found for '{parameters.ProjectFile}'; " +
                "re-scan deferred until the project loads.");
        }
    }

    /// <summary>Looks up every project that claims <paramref name="uri"/> via the membership index (does not fall back to folder-prefix matching).</summary>
    public IReadOnlyCollection<LspReqnrollProject> GetProjectsForUri(DocumentUri uri)
    {
        var filePath = NormaliseFilePath(uri.GetFileSystemPath() ?? string.Empty);
        if (string.IsNullOrEmpty(filePath))
            return [];

        List<ProjectKey> keySnapshot;
        lock (_membershipLock)
        {
            if (!_membership.TryGetValue(filePath, out var owners) || owners.Count == 0)
                return [];
            keySnapshot = owners.Keys.ToList();
        }

        var result = new List<LspReqnrollProject>(keySnapshot.Count);
        foreach (var key in keySnapshot)
        {
            var project = _findProjectByKey(key);
            if (project is not null)
                result.Add(project);
        }
        return result;
    }

    /// <summary>Returns whether <paramref name="filePath"/> (an already-resolved, non-empty local path) has any entry in the membership index at all.</summary>
    public bool IsPathOwned(string filePath)
    {
        var normalised = NormaliseFilePath(filePath);
        lock (_membershipLock)
        {
            return _membership.ContainsKey(normalised);
        }
    }

    /// <summary>Returns whether a baseline has been received for <paramref name="key"/>.</summary>
    public bool HasBaseline(ProjectKey key) => _baselineReceived.ContainsKey(key);

    /// <summary>Returns whether <paramref name="project"/> has received its initial full membership baseline yet.</summary>
    public bool HasBaselineForProject(LspReqnrollProject project)
        => HasBaseline(MakeKey(project));

    /// <summary>Returns every file path in the membership index that <paramref name="project"/> owns with the <see cref="ProjectFileRole.Feature"/> role.</summary>
    public IReadOnlyCollection<string> GetIndexedFeatureFiles(LspReqnrollProject project)
        => GetFilesByRole(project, ProjectFileRole.Feature);

    /// <summary>Returns every file path in the membership index that <paramref name="project"/> owns with the <see cref="ProjectFileRole.Binding"/> role.</summary>
    public IReadOnlyCollection<string> GetBindingFilePathsForProject(LspReqnrollProject project)
        => GetFilesByRole(project, ProjectFileRole.Binding);

    private IReadOnlyCollection<string> GetFilesByRole(LspReqnrollProject project, ProjectFileRole role)
    {
        var key = MakeKey(project);
        lock (_membershipLock)
        {
            return _membership
                .Where(kvp =>
                    kvp.Value.TryGetValue(key, out var r) &&
                    r == role)
                .Select(kvp => kvp.Key)
                .ToList();
        }
    }

    /// <summary>
    /// Removes and returns whether <paramref name="project"/> had a full re-scan deferred for it
    /// (its membership baseline arrived before the project itself registered — see
    /// <see cref="HandleProjectFilesAsync"/>). Callers (specifically
    /// <see cref="LspWorkspaceScopeManager.HandleProjectLoadedAsync"/>) are responsible for
    /// actually firing that re-scan when this returns <see langword="true"/>.
    /// </summary>
    public bool TryConsumePendingFullRescan(LspReqnrollProject project)
        => _pendingFullRescan.TryRemove(MakeKey(project), out _);

    private void ApplyDelta(ProjectKey key, ProjectFileEntry[] entries)
    {
        lock (_membershipLock)
        {
            foreach (var entry in entries)
            {
                var normPath = NormaliseFilePath(entry.Path);
                if (entry.Added)
                {
                    if (!_membership.TryGetValue(normPath, out var owners))
                    {
                        owners = new Dictionary<ProjectKey, ProjectFileRole>();
                        _membership[normPath] = owners;
                    }
                    owners[key] = entry.Role;
                }
                else
                {
                    if (_membership.TryGetValue(normPath, out var owners))
                    {
                        owners.Remove(key);
                        if (owners.Count == 0)
                            _membership.Remove(normPath);
                    }
                }
            }
        }
    }

    private static ProjectKey MakeKey(string projectFile, string tfm)
        => new(NormaliseFilePath(projectFile), tfm);

    private static ProjectKey MakeKey(LspReqnrollProject project)
        => new(NormaliseFilePath(project.ProjectFullName), project.TargetFrameworkMoniker);

    /// <summary>Normalises a file path for use as a membership-index key or <see cref="ProjectKey"/> component. Shared with <see cref="LspWorkspaceScopeManager.FindProjectByKey"/>, which must normalise the same way to match keys produced here.</summary>
    internal static string NormaliseFilePath(string path)
        => string.IsNullOrEmpty(path) ? path : Path.GetFullPath(path);
}
