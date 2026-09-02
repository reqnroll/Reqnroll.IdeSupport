using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// Cache of step binding matches keyed by <see cref="MatchSetKey"/> (document URI + owning
/// project), plus a reverse index from binding source locations back to the feature steps that
/// resolve to them.
/// </summary>
/// <remarks>
/// The cache is populated whenever a feature document is (re)parsed (see
/// <c>GherkinDocumentTaggerService</c>) and invalidated when the binding registry changes.
/// A shared/linked feature file may have one entry per owning project; present the primary
/// owner's entry for rendering (semantic tokens, diagnostics).
/// </remarks>
public interface IBindingMatchService
{
    /// <summary>Stores (replacing any prior entry for the same <see cref="FeatureBindingMatchSet.Key"/>).
    /// If the incoming key has a known <see cref="ProjectOwner"/>, evicts any
    /// <see cref="ProjectOwner.Unknown"/> placeholder for the same document.</summary>
    void Store(FeatureBindingMatchSet matchSet);

    /// <summary>Returns the cached match set for the given key, or <see cref="FeatureBindingMatchSet.Empty"/>.</summary>
    bool TryGet(MatchSetKey key, out FeatureBindingMatchSet matchSet);

    /// <summary>Drops all cached entries for the given document URI regardless of owner (used on DidClose).</summary>
    void InvalidateAllForDocument(string documentId);

    /// <summary>Drops all cached entries for the given project (used on project unload).</summary>
    void InvalidateAllForProject(ProjectOwner owner);

    /// <summary>Drops all cached match sets (emergency reset).</summary>
    void InvalidateAll();

    /// <summary>
    /// Returns every cached feature step that resolves to a binding at <paramref name="bindingLocation"/>.
    /// Pass <paramref name="projectFilter"/> to restrict results to specific owning projects;
    /// pass <see langword="null"/> to search across all projects.
    /// <see cref="ProjectOwner.Unknown"/> entries are always included regardless of the filter
    /// (they are pre-baseline placeholders that are visible to all callers).
    /// Backs Find Usages and the Code Lens usage counts.
    /// </summary>
    IReadOnlyList<StepBindingMatch> FindUsages(
        SourceLocation bindingLocation,
        IReadOnlyCollection<ProjectOwner>? projectFilter = null);

    /// <summary>
    /// Returns every cached feature step that resolves to the binding identified by
    /// <paramref name="bindingId"/> — a direct O(1) reverse-index lookup, with no location math
    /// (issue #471). Prefer this overload over the <see cref="SourceLocation"/> one whenever a
    /// <see cref="Bindings.ProjectStepDefinitionBinding"/> (and therefore its
    /// <see cref="BindingId"/>) is already in hand. Same project-filtering semantics as the
    /// <see cref="SourceLocation"/> overload.
    /// </summary>
    IReadOnlyList<StepBindingMatch> FindUsages(
        BindingId bindingId,
        IReadOnlyCollection<ProjectOwner>? projectFilter = null);

    /// <summary>
    /// Returns every cached match set across the whole workspace (closed files included, per the
    /// class remarks above), optionally restricted to specific owning projects. Backs the
    /// hook-match-count CodeLens (issue #373), which needs every project scenario's tag context
    /// to evaluate a hook's scope against, not just one document's.
    /// <see cref="ProjectOwner.Unknown"/> entries are always included regardless of the filter,
    /// matching <see cref="FindUsages"/>'s existing pre-baseline-placeholder convention.
    /// </summary>
    IEnumerable<FeatureBindingMatchSet> GetAll(IReadOnlyCollection<ProjectOwner>? projectFilter = null);

    /// <summary>
    /// Cheap, approximate cache-size snapshot for diagnostics (issue #471 investigation):
    /// <c>DocumentCount</c> is O(1); <c>TotalStepCount</c> is O(cached documents) — proportional to
    /// how many feature files are cached, not to their contents' binding-match complexity, so it
    /// stays negligible next to the O(bindings × cached steps) cost of a <see cref="FindUsages"/>
    /// sweep it's meant to be logged alongside. Not on any hot per-binding path.
    /// </summary>
    (int DocumentCount, int TotalStepCount) GetCacheStats();

    /// <summary>
    /// Issue #554 diagnostic: audits the invariant that the reverse index contains exactly the
    /// steps of the match sets currently held in the cache, and that a document has at most one
    /// live entry per owning project. Returns one human-readable line per anomaly (empty when the
    /// index is consistent). O(cached steps) — call it only when a caller has already observed a
    /// suspicious result (e.g. a duplicated usage), never on a hot path.
    /// </summary>
    IReadOnlyList<string> AuditIndexConsistency();
}
