using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;


using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tagging;

/// <summary>Default implementation of <see cref="IGherkinDocumentTaggerService"/> that parses Gherkin tags and maintains binding match sets for feature documents.</summary>
public class GherkinDocumentTaggerService : IGherkinDocumentTaggerService
{
    private readonly IDeveroomTagParser            _tagParser;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly ISemanticTokenService         _semanticTokenService;
    private readonly IBindingMatchService          _bindingMatchService;
    private readonly IIdeSupportLogger               _logger;
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IFileSystemForIDE             _fileSystem;

    /// <summary>Creates the tagger service with its collaborating document, registry, and match-set dependencies.</summary>
    public GherkinDocumentTaggerService(
        IDocumentBufferService        documentBufferService,
        IDeveroomTagParser            tagParser,
        IProjectBindingRegistryLookup registryLookup,
        ISemanticTokenService         semanticTokenService,
        IBindingMatchService          bindingMatchService,
        ILspWorkspaceScopeManager     scopeManager,
        IIdeSupportLogger               logger,
        IFileSystemForIDE             fileSystem)
    {
        _documentBufferService = documentBufferService;
        _tagParser             = tagParser;
        _registryLookup        = registryLookup;
        _semanticTokenService  = semanticTokenService;
        _bindingMatchService   = bindingMatchService;
        _scopeManager          = scopeManager;
        _logger                = logger;
        _fileSystem            = fileSystem;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<DeveroomTag>> ParseAsync(DocumentUri uri, int? version)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer))
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());

        var snapshot = buffer?.ToGherkinTextSnapshot();

        if (snapshot == null)
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());

        if (version.HasValue && snapshot.Version != version)
        {
            _logger.LogWarning($"Version mismatch for document {uri}: expected {version}, got {snapshot.Version}");
            return Task.FromResult<IReadOnlyCollection<DeveroomTag>>(Array.Empty<DeveroomTag>());
        }

        // Route to the per-project binding registry for this document URI.
        // Returns ProjectBindingRegistry.Invalid when the project has not yet been
        // discovered or its first discovery run has not completed; DeveroomTagParser
        // gracefully skips step-matching in that case.
        var registry = _registryLookup.GetRegistryForUri(uri);
        var tags     = _tagParser.Parse(snapshot, registry);
        _logger.LogInfo($"Parsed {tags.Count} tags from document {uri}");

        // Store the new tags first so semantic-token encoding (which re-reads them) and the
        // match set below both observe the same tag collection.
        _documentBufferService.UpdateTags(uri, tags);

        // Build the match set keyed by (uri, primaryOwner). If the primary owner is not yet
        // known (no baseline received), store with ProjectOwner.Unknown so diagnostics can
        // still work during startup. The Store call will evict the Unknown entry once a
        // project-keyed entry arrives for the same document.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        var matchSet = FeatureBindingMatchSet.FromTags(
            uri.ToString(), snapshot.Version, registry.Version, tags, owner);
        _bindingMatchService.Store(matchSet);

        // Evict the semantic token cache for this URI. The cache is keyed on (uri, documentVersion);
        // it must be invalidated here because binding discovery can update the tags for a document
        // whose version has not changed. Failure to evict would cause the client to receive
        // stale (pre-binding) token data indefinitely.
        _semanticTokenService.InvalidateCache(uri);

        return Task.FromResult(tags);
    }

    /// <inheritdoc/>
    public Task ScanClosedFileAsync(DocumentUri uri, string text, LspReqnrollProject project)
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

        var tags = _tagParser.Parse(snapshot, registry);

        var owner    = new ProjectOwner(project.ProjectFullName, project.TargetFrameworkMoniker);
        var matchSet = FeatureBindingMatchSet.FromTags(
            uri.ToString(), documentVersion: null, registry.Version, tags, owner);
        _bindingMatchService.Store(matchSet);

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

        // A feature file can be linked into more than one project; repopulate the match set for
        // each owner so (uri, project)-keyed lookups stay complete after the buffer is gone.
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
        {
            _logger.LogVerbose($"RescanClosedFile: '{uri}' has no owning project; skipping.");
            return;
        }

        foreach (var project in owners)
            await ScanClosedFileAsync(uri, text, project).ConfigureAwait(false);
    }
}
