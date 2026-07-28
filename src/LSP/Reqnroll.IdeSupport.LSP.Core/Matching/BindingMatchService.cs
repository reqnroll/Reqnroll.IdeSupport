#nullable enable

using System.Collections.Concurrent;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <inheritdoc cref="IBindingMatchService"/>
public sealed class BindingMatchService : IBindingMatchService
{
    private readonly ConcurrentDictionary<MatchSetKey, FeatureBindingMatchSet> _cache = new();

    /// <summary>Caches the given match set under its key, evicting any pre-baseline "Unknown" placeholder for the same document once a project-keyed entry arrives.</summary>
    public void Store(FeatureBindingMatchSet matchSet)
    {
        if (matchSet == null) throw new ArgumentNullException(nameof(matchSet));
        _cache[matchSet.Key] = matchSet;
        // When a project-keyed entry arrives, evict any Unknown placeholder for the same document
        // so the transition from pre-baseline to post-baseline state is clean.
        if (matchSet.Key.Owner.IsKnown)
            _cache.TryRemove(MatchSetKey.ForUnknownProject(matchSet.Key.DocumentId), out _);
    }

    /// <summary>Looks up the cached match set for the given key, returning <see cref="FeatureBindingMatchSet.Empty"/> and <see langword="false"/> on a miss.</summary>
    public bool TryGet(MatchSetKey key, out FeatureBindingMatchSet matchSet)
    {
        if (_cache.TryGetValue(key, out var found))
        {
            matchSet = found;
            return true;
        }

        matchSet = FeatureBindingMatchSet.Empty;
        return false;
    }

    /// <summary>Removes all cached match sets for the given document, across every project owner.</summary>
    public void InvalidateAllForDocument(string documentId)
    {
        if (string.IsNullOrEmpty(documentId))
            return;

        foreach (var key in _cache.Keys.Where(k =>
            string.Equals(k.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>Removes all cached match sets owned by the given project (matched by project file and target framework).</summary>
    public void InvalidateAllForProject(ProjectOwner owner)
    {
        if (!owner.IsKnown)
            return;

        foreach (var key in _cache.Keys.Where(k =>
            string.Equals(k.Owner.ProjectFile, owner.ProjectFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(k.Owner.Tfm,         owner.Tfm,         StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>Removes every cached match set, for every document and project owner.</summary>
    public void InvalidateAll() => _cache.Clear();

    /// <summary>Finds all cached step matches that resolve to the given binding source location, optionally restricted to the given projects.</summary>
    public IReadOnlyList<StepBindingMatch> FindUsages(
        SourceLocation bindingLocation,
        IReadOnlyCollection<ProjectOwner>? projectFilter = null)
    {
        if (bindingLocation == null)
            return Array.Empty<StepBindingMatch>();

        var usages = new List<StepBindingMatch>();

        // ConcurrentDictionary enumeration is safe under concurrent writes.
        foreach (var pair in _cache)
        {
            var key = pair.Key;
            var set = pair.Value;
            // Unknown entries are pre-baseline placeholders — always include them so
            // Find Usages works during the transition before the first baseline arrives.
            if (projectFilter != null && key.Owner.IsKnown && !MatchesFilter(key.Owner, projectFilter))
                continue;

            foreach (var step in set.Steps)
                if (step.BindingLocations.Any(loc => SameLocation(loc, bindingLocation)))
                    usages.Add(step);
        }

        return usages;
    }

    /// <inheritdoc />
    public IEnumerable<FeatureBindingMatchSet> GetAll(IReadOnlyCollection<ProjectOwner>? projectFilter = null)
    {
        // ConcurrentDictionary enumeration is safe under concurrent writes -- same guarantee
        // FindUsages already relies on.
        foreach (var pair in _cache)
        {
            var key = pair.Key;
            if (projectFilter != null && key.Owner.IsKnown && !MatchesFilter(key.Owner, projectFilter))
                continue;

            yield return pair.Value;
        }
    }

    private static bool MatchesFilter(ProjectOwner owner, IReadOnlyCollection<ProjectOwner> filter)
    {
        foreach (var f in filter)
        {
            if (string.Equals(f.ProjectFile, owner.ProjectFile, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.Tfm,         owner.Tfm,         StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool SameLocation(SourceLocation a, SourceLocation b)
    {
        if (!string.Equals(a.SourceFile, b.SourceFile, StringComparison.OrdinalIgnoreCase))
            return false;
        // Roslyn-path bindings span [attribute-line, body-end]; connector-path bindings only
        // store the method-body start (no end). Allow up to 2 lines of backward leeway so a
        // caret placed on the binding attribute (typically 1-2 lines above the body start)
        // resolves correctly against connector-path entries.
        var endLine = a.SourceFileEndLine ?? a.SourceFileLine;
        const int attributeLeeway = 2;
        return b.SourceFileLine >= (a.SourceFileLine - attributeLeeway)
               && b.SourceFileLine <= endLine;
    }
}
