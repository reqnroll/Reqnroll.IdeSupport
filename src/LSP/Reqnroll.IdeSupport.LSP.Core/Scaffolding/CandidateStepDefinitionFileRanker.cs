#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Ranks the existing <c>.cs</c> binding files that already contain step definitions matched to a
/// specific feature, so "Define missing step" can offer to append to a file the user is already
/// using for this feature instead of always scaffolding a new one.
/// </summary>
public static class CandidateStepDefinitionFileRanker
{
    /// <summary>
    /// Returns the source files backing <paramref name="matchSet"/>'s <see cref="FeatureBindingMatchSet.Defined"/>
    /// steps, ordered by how many of this feature's steps are bound there (most first). A file
    /// with more matched bindings for this feature is a stronger candidate for appending a new
    /// step than one with fewer, regardless of how many binding files exist elsewhere in the
    /// project. Returns an empty list when the feature has no defined steps yet (e.g. a brand-new
    /// feature) — callers should fall back to a project-wide heuristic in that case.
    /// </summary>
    public static IReadOnlyList<string> RankCandidateFiles(FeatureBindingMatchSet matchSet) =>
        matchSet.Defined
            .SelectMany(s => s.BindingLocations)
            .Select(loc => loc.SourceFile)
            .Where(f => !string.IsNullOrEmpty(f))
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
}
