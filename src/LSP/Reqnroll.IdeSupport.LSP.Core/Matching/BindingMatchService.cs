#nullable enable

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <inheritdoc cref="IBindingMatchService"/>
/// <remarks>
/// Issue #471: <see cref="FindUsages(BindingId,IReadOnlyCollection{ProjectOwner})"/> and
/// <see cref="FindUsages(SourceLocation,IReadOnlyCollection{ProjectOwner})"/> used to be an
/// unindexed full scan over <c>_cache</c>. They're now backed by two indexes maintained
/// alongside <c>_cache</c>, borrowing clangd's index shape (<c>index/Ref.h</c>/<c>FileIndex</c>):
/// a reverse index from stable <see cref="BindingId"/> identity to the steps that reference it
/// (clangd's <c>Refs</c> table), and a per-file location index used only to translate a raw
/// <see cref="SourceLocation"/> into a <see cref="BindingId"/> for callers that don't already
/// have a binding object in hand.
/// </remarks>
public sealed class BindingMatchService : IBindingMatchService
{
    private readonly ConcurrentDictionary<MatchSetKey, FeatureBindingMatchSet> _cache = new();

    /// <summary>Reverse index: binding identity -> every cached step that resolves to it (clangd's <c>Refs</c> table).</summary>
    private readonly ConcurrentDictionary<BindingId, ImmutableHashSet<IndexedStep>> _reverseIndex = new();

    /// <summary>
    /// Per-file binding-location index, sorted by <see cref="LocationEntry.StartLine"/> to support
    /// binary search, used only to translate a raw <see cref="SourceLocation"/> into a
    /// <see cref="BindingId"/> (design point 4 — the position-to-binding query; at the real
    /// repro's ~1,300 bindings/file, binary search measures ~40x faster than a linear scan of the
    /// same file's entries). Upsert-only by design: entries are keyed by the binding's stable
    /// identity, so a re-parse naturally overwrites a moved binding's span under the same key; a
    /// genuinely removed/renamed binding leaves a harmless stale entry that resolves to zero
    /// usages via <see cref="_reverseIndex"/> rather than a wrong answer (the same tolerance
    /// clangd's own background index has for staleness).
    /// </summary>
    private readonly ConcurrentDictionary<string, ImmutableArray<LocationEntry>> _locationIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One entry in the reverse index: the step, plus the match-set key it came from (needed to apply project-owner filtering).</summary>
    private readonly record struct IndexedStep(MatchSetKey Key, StepBindingMatch Step);

    /// <summary>One entry in the per-file location index: the binding's source line span and identity.</summary>
    private readonly record struct LocationEntry(int StartLine, int EndLine, BindingId Id);

    /// <summary>
    /// Guards the cache-plus-index invariant: the reverse index holds exactly the steps of the
    /// match sets currently in <see cref="_cache"/> (issue #554). Each collection is individually
    /// thread-safe, but a store or an invalidation spans all three, so without this lock two
    /// concurrent writers for the same key can interleave — one removing the previous entry's
    /// index contributions while the other is still adding its own — and leave the losing match
    /// set's steps orphaned in the reverse index. That orphan is invisible to every later write
    /// (a <see cref="StepBindingMatch"/> compares by reference, so nothing dedups or evicts it)
    /// and permanently adds one phantom usage per binding of that document, which is what #554
    /// reported: four step-usage CodeLens counts one too high for a whole server-process
    /// lifetime, on some launches and not others. That happens for real because
    /// <c>GherkinDocumentTaggerService.ParseAsync</c> is reachable for one open document from
    /// four unsynchronised pipeline entry points (<c>TextDocumentSyncHandler</c>,
    /// <c>DocumentActivatedHandler</c>, <c>ReqnrollConfigChangedHandler</c> and
    /// <c>BindingRegistryChangedHandler</c> — only the last serialises through
    /// <c>ParseCoordinator</c>), and the startup burst routinely parses the same file twice
    /// within a few milliseconds on different threads.
    /// Writers are exclusive; readers (<see cref="FindUsages(BindingId,IReadOnlyCollection{ProjectOwner})"/>
    /// and friends) share, so the per-binding CodeLens sweep still runs fully in parallel with
    /// itself — it only excludes the comparatively rare write. Deliberately not disposed: this
    /// service is a process-lifetime DI singleton, so there is no point at which releasing the
    /// lock would be safe or useful.
    /// </summary>
    private readonly ReaderWriterLockSlim _sync = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Caches the given match set under its key, evicting any pre-baseline "Unknown" placeholder for the same document once a project-keyed entry arrives.</summary>
    public void Store(FeatureBindingMatchSet matchSet)
    {
        if (matchSet == null) throw new ArgumentNullException(nameof(matchSet));

        // Replace-in-place must be atomic against every other writer for this key: see _sync.
        _sync.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(matchSet.Key, out var previous))
                RemoveFromReverseIndex(matchSet.Key, previous);

            _cache[matchSet.Key] = matchSet;
            AddToIndexes(matchSet.Key, matchSet);

            // When a project-keyed entry arrives, evict any Unknown placeholder for the same document
            // so the transition from pre-baseline to post-baseline state is clean.
            if (matchSet.Key.Owner.IsKnown)
            {
                var unknownKey = MatchSetKey.ForUnknownProject(matchSet.Key.DocumentId);
                if (_cache.TryRemove(unknownKey, out var unknownPlaceholder))
                    RemoveFromReverseIndex(unknownKey, unknownPlaceholder);
            }
        }
        finally
        {
            _sync.ExitWriteLock();
        }
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

        _sync.EnterWriteLock();
        try
        {
            foreach (var key in _cache.Keys.Where(k =>
                string.Equals(k.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (_cache.TryRemove(key, out var removed))
                    RemoveFromReverseIndex(key, removed);
            }
        }
        finally
        {
            _sync.ExitWriteLock();
        }
    }

    /// <summary>Removes all cached match sets owned by the given project (matched by project file and target framework).</summary>
    public void InvalidateAllForProject(ProjectOwner owner)
    {
        if (!owner.IsKnown)
            return;

        _sync.EnterWriteLock();
        try
        {
            foreach (var key in _cache.Keys.Where(k =>
                string.Equals(k.Owner.ProjectFile, owner.ProjectFile, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(k.Owner.Tfm,         owner.Tfm,         StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (_cache.TryRemove(key, out var removed))
                    RemoveFromReverseIndex(key, removed);
            }
        }
        finally
        {
            _sync.ExitWriteLock();
        }
    }

    /// <summary>Removes every cached match set, for every document and project owner.</summary>
    public void InvalidateAll()
    {
        _sync.EnterWriteLock();
        try
        {
            _cache.Clear();
            _reverseIndex.Clear();
            _locationIndex.Clear();
        }
        finally
        {
            _sync.ExitWriteLock();
        }
    }

    /// <summary>Finds all cached step matches that resolve to the given binding source location, optionally restricted to the given projects.</summary>
    public IReadOnlyList<StepBindingMatch> FindUsages(
        SourceLocation bindingLocation,
        IReadOnlyCollection<ProjectOwner>? projectFilter = null)
    {
        if (bindingLocation == null)
            return Array.Empty<StepBindingMatch>();

        // Shared read: this must not observe a store's index update half-applied (issue #554),
        // but it does run concurrently with every other reader.
        _sync.EnterReadLock();
        try
        {
            if (!_locationIndex.TryGetValue(bindingLocation.SourceFile, out var entries) || entries.IsEmpty)
                return Array.Empty<StepBindingMatch>();

            // Roslyn-path bindings span [attribute-line, body-end]; connector-path bindings only
            // store the method-body start (no end). Allow up to 2 lines of backward leeway so a
            // caret placed on the binding attribute (typically 1-2 lines above the body start)
            // resolves correctly -- same leeway the pre-index SameLocation check used.
            const int attributeLeeway = 2;
            var line = bindingLocation.SourceFileLine;

            var matchedIds = FindContainingIds(entries, line, attributeLeeway);
            if (matchedIds.Count == 0)
                return Array.Empty<StepBindingMatch>();

            var usages = new List<StepBindingMatch>();
            foreach (var id in matchedIds)
                CollectUsages(id, projectFilter, usages);
            return usages;
        }
        finally
        {
            _sync.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StepBindingMatch> FindUsages(
        BindingId bindingId,
        IReadOnlyCollection<ProjectOwner>? projectFilter = null)
    {
        var usages = new List<StepBindingMatch>();
        _sync.EnterReadLock();
        try
        {
            CollectUsages(bindingId, projectFilter, usages);
        }
        finally
        {
            _sync.ExitReadLock();
        }
        return usages;
    }

    /// <inheritdoc />
    public IEnumerable<FeatureBindingMatchSet> GetAll(IReadOnlyCollection<ProjectOwner>? projectFilter = null)
    {
        // Materialised rather than yielded lazily: the read lock (issue #554) must not be held
        // across the consumer's own work, and callers enumerate this once into a list anyway.
        var matched = new List<FeatureBindingMatchSet>();

        _sync.EnterReadLock();
        try
        {
            foreach (var pair in _cache)
            {
                var key = pair.Key;
                if (projectFilter != null && key.Owner.IsKnown && !MatchesFilter(key.Owner, projectFilter))
                    continue;

                matched.Add(pair.Value);
            }
        }
        finally
        {
            _sync.ExitReadLock();
        }

        return matched;
    }

    /// <inheritdoc />
    public (int DocumentCount, int TotalStepCount) GetCacheStats()
    {
        // Snapshot the values once rather than enumerating _cache twice (Count + Sum), so this
        // stays a single O(cached documents) pass under concurrent Store/Invalidate calls.
        _sync.EnterReadLock();
        try
        {
            var snapshot = _cache.Values.ToArray();
            return (snapshot.Length, snapshot.Sum(s => s.Steps.Count));
        }
        finally
        {
            _sync.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AuditIndexConsistency()
    {
        var anomalies = new List<string>();

        _sync.EnterReadLock();
        try
        {
            // Snapshot the cache once: what the reverse index is allowed to contain.
            var cached = _cache.ToArray();
            var live = new HashSet<(MatchSetKey Key, StepBindingMatch Step)>();
            foreach (var pair in cached)
                foreach (var step in pair.Value.Steps)
                    live.Add((pair.Key, step));

            // 1. Orphans: reverse-index entries whose (key, step) pair is no longer in the cache --
            //    the signature of a Store call whose index writes survived its own cache entry being
            //    replaced by a concurrent Store for the same key (the read-then-write race).
            foreach (var indexPair in _reverseIndex)
            {
                foreach (var indexed in indexPair.Value)
                {
                    if (live.Contains((indexed.Key, indexed.Step)))
                        continue;

                    var reason = _cache.ContainsKey(indexed.Key)
                        ? "step is not in the cached match set for that key (superseded set still indexed)"
                        : "no cached match set for that key at all";
                    anomalies.Add(
                        $"orphaned reverse-index entry: binding={indexPair.Key} doc={indexed.Step.FeatureDocumentId} " +
                        $"line={indexed.Step.Range.StartLinePosition.Line} key.owner=" +
                        $"'{(indexed.Key.Owner.IsKnown ? indexed.Key.Owner.ProjectFile : "<Unknown>")}|{indexed.Key.Owner.Tfm}' — {reason}");
                }
            }

            // 2. Duplicate live entries for one document: a surviving Unknown placeholder alongside a
            //    project-keyed entry, or two owner keys for the same document (e.g. the same project
            //    seen with a different TFM). Both double every usage count for that document.
            foreach (var group in cached.GroupBy(p => p.Key.DocumentId, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() < 2)
                    continue;

                var owners = string.Join(", ", group.Select(p =>
                    $"'{(p.Key.Owner.IsKnown ? p.Key.Owner.ProjectFile : "<Unknown>")}|{p.Key.Owner.Tfm}' ({p.Value.Steps.Count} step(s))"));
                anomalies.Add(
                    $"document cached under {group.Count()} keys: doc={group.Key} owners=[{owners}] " +
                    "(expected for a feature linked into several projects; a bug when the same project " +
                    "appears twice, differs only by TFM, or an <Unknown> placeholder survived)");
            }
        }
        finally
        {
            _sync.ExitReadLock();
        }

        return anomalies;
    }

    private void CollectUsages(
        BindingId id, IReadOnlyCollection<ProjectOwner>? projectFilter, List<StepBindingMatch> usages)
    {
        if (!_reverseIndex.TryGetValue(id, out var indexedSteps))
            return;

        foreach (var indexed in indexedSteps)
        {
            // Unknown entries are pre-baseline placeholders -- always include them so
            // Find Usages works during the transition before the first baseline arrives.
            if (projectFilter != null && indexed.Key.Owner.IsKnown && !MatchesFilter(indexed.Key.Owner, projectFilter))
                continue;

            usages.Add(indexed.Step);
        }
    }

    private static List<BindingId> FindContainingIds(ImmutableArray<LocationEntry> entries, int line, int leeway)
    {
        // entries is sorted ascending by StartLine (see UpsertLocationIndex). Step-definition
        // bindings within one C# file are non-overlapping -- methods can't nest -- so sorting by
        // StartLine also makes EndLine monotonically non-decreasing; that lets a single binary
        // search bound the scan on both sides without needing a full interval tree.
        var result = new List<BindingId>();
        if (entries.IsEmpty)
            return result;

        // Binary search for the first index whose StartLine is past the leeway window -- nothing
        // at or after it can satisfy `line >= entry.StartLine - leeway`.
        var lo = 0;
        var hi = entries.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (entries[mid].StartLine <= line + leeway) lo = mid + 1; else hi = mid;
        }

        // Scan backward from the boundary, relying on EndLine's monotonicity to stop early. A
        // small miss tolerance (rather than breaking on the very first miss) hedges against the
        // rare case of a connector-discovered entry whose EndLine collapses to its StartLine
        // (connector bindings carry no method-end line) interrupting the monotonic run.
        const int missTolerance = 4;
        var misses = 0;
        for (var i = lo - 1; i >= 0 && misses <= missTolerance; i--)
        {
            var entry = entries[i];
            if (entry.EndLine >= line)
            {
                result.Add(entry.Id);
                misses = 0;
            }
            else
            {
                misses++;
            }
        }
        return result;
    }

    private void AddToIndexes(MatchSetKey key, FeatureBindingMatchSet matchSet)
    {
        // Batch the location-index writes per file (one sort per referenced .cs file, not one per
        // step) -- a large feature file can carry thousands of steps referencing the same handful
        // of files, and re-sorting a ~1,300-entry file array on every single step would turn the
        // O(log n) binary search above into an O(n log n) write amplification on every Store call.
        Dictionary<string, List<LocationEntry>>? newLocationsByFile = null;

        foreach (var step in matchSet.Steps)
        {
            foreach (var (id, location) in step.BindingIdentities)
            {
                AddToReverseIndex(id, new IndexedStep(key, step));

                if (string.IsNullOrEmpty(location.SourceFile))
                    continue;

                newLocationsByFile ??= new Dictionary<string, List<LocationEntry>>(StringComparer.OrdinalIgnoreCase);
                if (!newLocationsByFile.TryGetValue(location.SourceFile, out var list))
                    newLocationsByFile[location.SourceFile] = list = new List<LocationEntry>();

                var endLine = location.SourceFileEndLine ?? location.SourceFileLine;
                list.Add(new LocationEntry(location.SourceFileLine, endLine, id));
            }
        }

        if (newLocationsByFile != null)
            foreach (var pair in newLocationsByFile)
                UpsertLocationIndex(pair.Key, pair.Value);
    }

    private void RemoveFromReverseIndex(MatchSetKey key, FeatureBindingMatchSet matchSet)
    {
        foreach (var step in matchSet.Steps)
            foreach (var (id, _) in step.BindingIdentities)
                RemoveFromReverseIndex(id, new IndexedStep(key, step));
    }

    private void AddToReverseIndex(BindingId id, IndexedStep indexedStep)
    {
        _reverseIndex.AddOrUpdate(id,
            _ => ImmutableHashSet.Create(indexedStep),
            (_, existing) => existing.Add(indexedStep));
    }

    private void RemoveFromReverseIndex(BindingId id, IndexedStep indexedStep)
    {
        var updated = _reverseIndex.AddOrUpdate(id,
            _ => ImmutableHashSet<IndexedStep>.Empty,
            (_, existing) => existing.Remove(indexedStep));

        if (updated.IsEmpty)
            ((ICollection<KeyValuePair<BindingId, ImmutableHashSet<IndexedStep>>>)_reverseIndex)
                .Remove(new KeyValuePair<BindingId, ImmutableHashSet<IndexedStep>>(id, updated));
    }

    private void UpsertLocationIndex(string file, List<LocationEntry> newEntries)
    {
        _locationIndex.AddOrUpdate(file,
            _ => Sorted(DedupeById(newEntries)),
            (_, existing) =>
            {
                // Replace any prior entry sharing a BindingId with an incoming one (its location
                // may have shifted since the last store) rather than accumulating duplicates.
                var incomingIds = new HashSet<BindingId>(newEntries.Select(e => e.Id));
                var merged = existing.Where(e => !incomingIds.Contains(e.Id))
                                      .Concat(DedupeById(newEntries));
                return Sorted(merged);
            });
    }

    private static IEnumerable<LocationEntry> DedupeById(List<LocationEntry> entries)
    {
        // The same binding can appear more than once in a single Store call (e.g. two feature
        // steps matching the same step definition) -- last-write-wins is fine, they're the same
        // binding at the same location.
        return entries
            .GroupBy(e => e.Id)
            .Select(g => g.Last());
    }

    private static ImmutableArray<LocationEntry> Sorted(IEnumerable<LocationEntry> entries) =>
        entries.OrderBy(e => e.StartLine).ToImmutableArray();

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
}
