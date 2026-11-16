using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;


using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tagging;

/// <summary>Default implementation of <see cref="IGherkinDocumentTaggerService"/> that parses Gherkin tags and maintains binding match sets for feature documents.</summary>
public class GherkinDocumentTaggerService : IGherkinDocumentTaggerService
{
    private readonly IIdeSupportTagParser            _tagParser;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IBindingMatchService          _bindingMatchService;
    private readonly IIdeSupportLogger               _logger;
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IFileSystemForIDE             _fileSystem;
    private readonly IDocumentGenerations          _documentGenerations;
    private readonly IProjectConfigGenerations     _configGenerations;

    /// <summary>Creates the tagger service with its collaborating document, registry, and match-set dependencies.</summary>
    public GherkinDocumentTaggerService(
        IDocumentBufferService        documentBufferService,
        IIdeSupportTagParser            tagParser,
        IProjectBindingRegistryLookup registryLookup,
        IBindingMatchService          bindingMatchService,
        ILspWorkspaceScopeManager     scopeManager,
        IIdeSupportLogger               logger,
        IFileSystemForIDE             fileSystem,
        IDocumentGenerations          documentGenerations,
        IProjectConfigGenerations     configGenerations)
    {
        _documentBufferService = documentBufferService;
        _tagParser             = tagParser;
        _registryLookup        = registryLookup;
        _bindingMatchService   = bindingMatchService;
        _scopeManager          = scopeManager;
        _logger                = logger;
        _fileSystem            = fileSystem;
        _documentGenerations   = documentGenerations;
        _configGenerations     = configGenerations;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<IdeSupportTag>> ParseAsync(DocumentUri uri, int? version)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer is null)
            return Task.FromResult<IReadOnlyCollection<IdeSupportTag>>(Array.Empty<IdeSupportTag>());

        var snapshot = buffer.ToGherkinTextSnapshot();

        if (snapshot == null)
            return Task.FromResult<IReadOnlyCollection<IdeSupportTag>>(Array.Empty<IdeSupportTag>());

        if (version.HasValue && snapshot.Version != version)
        {
            _logger.LogWarning($"Version mismatch for document {uri}: expected {version}, got {snapshot.Version}");
            return Task.FromResult<IReadOnlyCollection<IdeSupportTag>>(Array.Empty<IdeSupportTag>());
        }

        // Route to the per-project binding registry for this document URI.
        // Returns ProjectBindingRegistry.Invalid when the project has not yet been
        // discovered or its first discovery run has not completed; IdeSupportTagParser
        // gracefully skips step-matching in that case.
        var registry     = _registryLookup.GetRegistryForUri(uri);
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);

        // If the primary owner is not yet known (no baseline received), the match set is stored
        // with ProjectOwner.Unknown so diagnostics can still work during startup. Store evicts the
        // Unknown entry once a project-keyed entry arrives for the same document.
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        // The stamp records each input's version as read before the input itself: the buffer's
        // generation came with the snapshot, the configuration generation is read before the
        // feature language, and the registry is the instance the tags are matched against.
        var stamp = new DerivationStamp(
            buffer.Generation,
            primaryOwner is not null ? _configGenerations.Current(owner) : 0,
            registry.Version);

        var tags = _tagParser.Parse(snapshot, registry, ProjectFeatureLanguage.For(primaryOwner));
        _logger.LogInfo($"Parsed {tags.Count} tags from document {uri}");

        // Store the new tags first so semantic-token encoding (which re-reads them) and the
        // match set below both observe the same tag collection.
        // The buffer may have been removed by didClose while the parse ran: the close
        // path has already invalidated and rescanned this document from disk, so drop the result
        // rather than overwrite that closed-file match set with one for a document no longer open.
        if (!_documentBufferService.UpdateTags(uri, stamp, tags))
        {
            _logger.LogVerbose($"Document {uri} was closed or changed during parsing; discarding parse result.");
            return Task.FromResult<IReadOnlyCollection<IdeSupportTag>>(Array.Empty<IdeSupportTag>());
        }

        // Keyed by (uri, primaryOwner); see the owner resolution above.
        var matchSet = FeatureBindingMatchSet.FromTags(
            uri.ToString(), snapshot.Version, stamp, tags, owner);
        // The store drops a derivation that is out of date and logs why; the tags above carry their own
        // stamp, so the semantic token cache keys on it and needs no eviction here.
        _bindingMatchService.Store(matchSet);

        return Task.FromResult(tags);
    }

    /// <inheritdoc/>
    public Task ScanClosedFileAsync(DocumentUri uri, string text, LspReqnrollProject project, long contentGeneration)
    {
        // Deterministic open-document guard. The handler that drives closed-file scans takes an
        // open-URI snapshot before this runs, but that snapshot is racy: a file (especially one
        // linked into multiple projects) can be opened between the snapshot and this call. The
        // match set is keyed by URI + project, so writing here would clobber the open document's
        // match set — with a null version and possibly a not-yet-discovered registry. If the
        // document is open, its own ParseAsync pipeline owns the match set; skip the closed scan.
        if (_documentBufferService.TryGet(uri, out _))
        {
            _logger.LogVerbose($"ScanClosedFile: '{uri}' is open; skipping closed-file scan.");
            return Task.CompletedTask;
        }

        var snapshot = new LspTextSnapshot(uri.ToString(), version: 0, text);

        // Use the project's own registry directly (bypasses the router) so that the match set
        // for a shared/linked feature is computed against THIS project's bindings, not the
        // primary owner's bindings. This is the per-(uri, project) correctness requirement
        // (primary-owner resolution / shared-feature scoping 2B).
        var registry = project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
                       && obj is ConnectorBindingRegistryProvider provider
            ? provider.Current
            : _registryLookup.GetRegistryForUri(uri);   // fallback: router (should not happen after baseline)

        // The configuration generation is read before the feature language; see ParseAsync.
        var owner = new ProjectOwner(project.ProjectFullName, project.TargetFrameworkMoniker);
        var stamp = new DerivationStamp(contentGeneration, _configGenerations.Current(owner), registry.Version);

        var tags = _tagParser.Parse(snapshot, registry, ProjectFeatureLanguage.For(project));

        var matchSet = FeatureBindingMatchSet.FromTags(
            uri.ToString(), documentVersion: null, stamp, tags, owner);
        if (_bindingMatchService.Store(matchSet) != StoreOutcome.Accepted)
        {
            _logger.LogVerbose($"ScanClosedFile: result for {uri} [{project.ProjectName}] was out of date; dropped.");
            return Task.CompletedTask;
        }

        _logger.LogVerbose($"ScanClosedFile: stored {matchSet.Steps.Count} step(s) for {uri} [{project.ProjectName}]");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task RescanClosedFileAsync(DocumentUri uri)
    {
        var path = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(path) || !_fileSystem.File.Exists(path))
        {
            _logger.LogVerbose($"RescanClosedFile: '{uri}' not found on disk; skipping.");
            return;
        }

        // Read before the text, so the stamp is never newer than the content it describes.
        var contentGeneration = _documentGenerations.Current(uri.ToString());

        string text;
        try
        {
            text = await _fileSystem.File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"RescanClosedFile: could not read '{path}': {ex.Message}");
            return;
        }

        await ScanClosedFileForAllOwnersAsync(uri, text, contentGeneration).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ScanClosedFileForAllOwnersAsync(DocumentUri uri, string text, long contentGeneration)
    {
        // A feature file can be linked into more than one project; repopulate the match set for
        // each owner so (uri, project)-keyed lookups stay complete after the buffer is gone.
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
        {
            _logger.LogVerbose($"ScanClosedFileForAllOwners: '{uri}' has no owning project; skipping.");
            return;
        }

        foreach (var project in owners)
            await ScanClosedFileAsync(uri, text, project, contentGeneration).ConfigureAwait(false);
    }
}
