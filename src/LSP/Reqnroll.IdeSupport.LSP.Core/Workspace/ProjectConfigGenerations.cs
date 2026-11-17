using System.Collections.Concurrent;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// Thread-safe <see cref="IProjectConfigGenerations"/>, keyed by <see cref="ProjectOwner"/> so it
/// agrees with the project dimension of match-set keys. Entries are kept for the life of the process.
/// </summary>
public sealed class ProjectConfigGenerations : IProjectConfigGenerations
{
    private readonly ConcurrentDictionary<ProjectOwner, long> _generations = new();

    /// <inheritdoc/>
    public long Current(ProjectOwner project)
        => _generations.TryGetValue(project, out var generation) ? generation : 0;

    /// <inheritdoc/>
    // See DocumentGenerations.Bump: AddOrUpdate re-reads on contention, so no increment is lost.
    public long Bump(ProjectOwner project)
        => _generations.AddOrUpdate(project, 1, (_, generation) => generation + 1);
}
