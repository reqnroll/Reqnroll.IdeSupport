using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// <see cref="IProjectFeatureFileLookup"/> backed by the server's membership index, so linked
/// feature files — ones the project owns but that live outside its folder — count.
/// </summary>
public sealed class MembershipIndexFeatureFileLookup : IProjectFeatureFileLookup
{
    private readonly ILspWorkspaceScopeManager _scopeManager;

    /// <summary>Initializes a new instance of the <see cref="MembershipIndexFeatureFileLookup"/> class.</summary>
    public MembershipIndexFeatureFileLookup(ILspWorkspaceScopeManager scopeManager)
    {
        _scopeManager = scopeManager;
    }

    /// <inheritdoc/>
    public bool? HasFeatureFiles(IProjectScope scope)
    {
        // Only the baseline is authoritative. Before it arrives the index legitimately reports
        // zero files for a project full of them (the race HandleProjectLoadedAsync defers a full
        // re-scan for), so "no baseline yet" has to surface as unknown rather than as "no feature
        // files" -- otherwise the very first discovery run after a solution opens would be
        // skipped for every project.
        if (scope is not LspReqnrollProject project || !_scopeManager.HasBaselineForProject(project))
            return null;

        return _scopeManager.GetIndexedFeatureFiles(project).Count > 0;
    }
}
