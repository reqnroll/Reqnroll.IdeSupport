using System.Collections.Concurrent;
using System.IO;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Registry;

/// <summary>
/// Feeds tag completion with the tags currently in use across the project(s) that own a
/// document (issue #828). Per project it keeps a per-file cache of tag-usage counts: open
/// documents are re-extracted from the live document buffer on every request (so a tag typed a
/// second ago in a sibling file is offered immediately), closed documents are parsed from disk
/// once and reused while their write time is unchanged, and the per-project file set is
/// re-checked against the membership index every request (so baseline deltas and disk changes
/// show up without any event wiring).
/// </summary>
public interface IFeatureTagRegistryProvider
{
    /// <summary>Returns the tags in use across the projects owning <paramref name="uri"/>, as candidate/usage-count pairs.</summary>
    Task<IReadOnlyCollection<StepCandidate>> GetTagCandidatesAsync(DocumentUri uri, CancellationToken ct);
}

/// <summary>Default implementation of <see cref="IFeatureTagRegistryProvider"/>.</summary>
public sealed class FeatureTagRegistryProvider : IFeatureTagRegistryProvider
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDocumentBufferService _bufferService;
    private readonly IIdeSupportTagParser _tagParser;
    private readonly IFileSystemForIDE _fileSystem;
    private readonly IIdeSupportLogger _logger;

    private readonly ConcurrentDictionary<string, ProjectTagIndex> _indexes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="FeatureTagRegistryProvider"/> class.</summary>
    public FeatureTagRegistryProvider(
        ILspWorkspaceScopeManager scopeManager,
        IDocumentBufferService bufferService,
        IIdeSupportTagParser tagParser,
        IFileSystemForIDE fileSystem,
        IIdeSupportLogger logger)
    {
        _scopeManager = scopeManager;
        _bufferService = bufferService;
        _tagParser = tagParser;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<StepCandidate>> GetTagCandidatesAsync(
        DocumentUri uri, CancellationToken ct)
    {
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
            return Array.Empty<StepCandidate>();

        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            var index = _indexes.GetOrAdd(Normalise(owner.ProjectFullName), _ => new ProjectTagIndex());
            await MergeProjectTagsAsync(index, owner, merged, ct).ConfigureAwait(false);
        }

        return merged
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new StepCandidate(kv.Key, kv.Value))
            .ToArray();
    }

    // ── Per-project merging ───────────────────────────────────────────────────

    private async Task MergeProjectTagsAsync(
        ProjectTagIndex index,
        LspReqnrollProject project,
        Dictionary<string, int> merged,
        CancellationToken ct)
    {
        var files = GetProjectFeatureFiles(project);
        if (files.Count == 0)
            return;

        // Drop cached entries for files the project no longer contains (baseline delta, or a
        // file deleted or excluded since the last request).
        var known = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var cachedPath in index.Files.Keys)
            if (!known.Contains(cachedPath))
                index.Files.TryRemove(cachedPath, out _);

        var perFile = await Task.WhenAll(files.Select(f => GetFileTagsAsync(index, f, ct)))
            .ConfigureAwait(false);

        foreach (var counts in perFile)
        {
            if (counts is null) continue;
            foreach (var pair in counts)
                merged[pair.Key] = merged.TryGetValue(pair.Key, out var existing)
                    ? existing + pair.Value
                    : pair.Value;
        }

        // One audible, once-per-project progression line; per-request traffic stays Verbose.
        if (Interlocked.CompareExchange(ref index.FirstScanDone, 1, 0) == 0)
            _logger.LogInfo(
                $"[TagRegistry] Indexed {files.Count} feature file(s) for tag completion in project '{project.ProjectName}'");
    }

    private IReadOnlyCollection<string> GetProjectFeatureFiles(LspReqnrollProject project)
    {
        // The membership baseline is the exact per-project file list (it also covers linked
        // feature files outside the project folder). Before the baseline has arrived, fall back
        // to a disk scan of the project folder, padded with the open feature buffers under it so
        // a brand-new, as-yet-unsaved file is immediately visible. Both are re-queried on every
        // request, so a later baseline or on-disk change is picked up without any event wiring.
        if (_scopeManager.HasBaselineForProject(project))
            return _scopeManager.GetIndexedFeatureFiles(project).Select(Normalise).ToArray();

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in project.GetProjectFiles(".feature"))
            files.Add(Normalise(file));
        foreach (var buffer in _bufferService.All)
        {
            var path = buffer.Uri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path) && PathUtils.IsUnderFolder(path, project.ProjectFolder))
                files.Add(Normalise(path));
        }
        return files;
    }

    // ── Per-file extraction ───────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<string, int>?> GetFileTagsAsync(
        ProjectTagIndex index,
        string filePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var uri = DocumentUri.FromFileSystemPath(filePath);

        // Open document: parse the live buffer text — never a disk cache, which could lag the
        // editor's current content. Cache by buffer version so an unedited file costs one
        // dictionary lookup, not a re-parse, on the next request.
        if (_bufferService.TryGet(uri, out var buffer) && buffer is not null)
        {
            if (buffer.Version.HasValue &&
                index.Files.TryGetValue(filePath, out var cached) &&
                cached.Version == buffer.Version.Value)
                return cached.Counts;

            var counts = CollectCounts(buffer.Text);
            if (buffer.Version.HasValue)
                index.Files[filePath] = new FileTagEntry(buffer.Version.Value, null, counts);
            return counts;
        }

        // Closed document: disk is the source of truth. Re-parse only when the file's last-write
        // time differs from the cached value, so an unchanged project costs one stat per file.
        if (!_fileSystem.File.Exists(filePath))
        {
            index.Files.TryRemove(filePath, out _);
            return null;
        }

        var lastWrite = _fileSystem.File.GetLastWriteTimeUtc(filePath);
        if (index.Files.TryGetValue(filePath, out var cachedEntry) &&
            cachedEntry.LastWriteTimeUtc == lastWrite)
            return cachedEntry.Counts;

        try
        {
            var text = await _fileSystem.File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var counts = CollectCounts(text);
            index.Files[filePath] = new FileTagEntry(null, lastWrite, counts);
            return counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogVerbose($"[TagRegistry] could not read '{filePath}': {ex.Message}");
            index.Files.TryRemove(filePath, out _);
            return null;
        }
    }

    /// <summary>Parses the given text against no binding registry and counts the tag names in it.</summary>
    private IReadOnlyDictionary<string, int> CollectCounts(string text)
    {
        var snapshot = new LspTextSnapshot(string.Empty, 0, text);
        var tags = _tagParser.Parse(snapshot, ProjectBindingRegistry.Invalid);
        return GherkinTagNameCollector.CollectCounts(tags);
    }

    private static string Normalise(string path) => MembershipIndex.NormaliseFilePath(path);

    // ── State ─────────────────────────────────────────────────────────────────

    private sealed class ProjectTagIndex
    {
        /// <summary>Guards the once-per-project Info scan log; see <see cref="FeatureTagRegistryProvider.MergeProjectTagsAsync"/>.</summary>
        public int FirstScanDone;

        public readonly ConcurrentDictionary<string, FileTagEntry> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cached tag counts for one feature file, valid while its identity anchor (buffer version or disk write time) is unchanged.</summary>
    private sealed record FileTagEntry(
        int? Version,
        DateTime? LastWriteTimeUtc,
        IReadOnlyDictionary<string, int> Counts);
}
