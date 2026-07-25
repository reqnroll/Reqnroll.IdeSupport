#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles <c>textDocument/definition</c> requests originating from a cursor position in a
/// <c>.feature</c> file (Go to Step Definition).
/// <para>
/// Implements <see cref="IDefinitionHandler"/> so OmniSharp registers the capability via
/// <c>client/registerCapability</c> (dynamic registration) after the handshake, scoped to
/// <c>**/*.feature</c> files only.
/// </para>
/// </summary>
public sealed class DefinitionHandler : IDefinitionHandler
{
    private readonly IBindingMatchService      _matchService;
    private readonly IDocumentBufferService    _bufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger           _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="DefinitionHandler"/> class.</summary>
    public DefinitionHandler(
        IBindingMatchService      matchService,
        IDocumentBufferService    bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger           logger,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService  = matchService;
        _bufferService = bufferService;
        _scopeManager  = scopeManager;
        _logger        = logger;
        _recorder      = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Builds the LSP registration options advertising go-to-definition support for <c>.feature</c> files.</summary>
    public DefinitionRegistrationOptions GetRegistrationOptions(
        DefinitionCapability    capability,
        ClientCapabilities      clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" })
        };

    /// <summary>Handles a <c>textDocument/definition</c> request for step-definition navigation.</summary>
    public Task<LocationOrLocationLinks?> Handle(
        DefinitionParams  request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): time the cache-hit definition round-trip (the handler's own work).
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentDefinition, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"DefinitionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"DefinitionHandler: no document buffer for {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(request.Position.Line, request.Position.Character);

        // Resolve the primary owner; fall back to Unknown for pre-baseline startup.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        var docId = uri.ToString();
        if (!_matchService.TryGet(new MatchSetKey(docId, owner), out var matchSet) || matchSet is null)
        {
            _logger.LogVerbose($"DefinitionHandler: no match set cached for {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var step = matchSet.FindAt(offset);
        if (step is null)
        {
            _logger.LogVerbose($"DefinitionHandler: no step at offset {offset} in {uri}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var locations = step.Result.Items
            .Select(item => item.MatchedStepDefinition?.Implementation)
            .Where(impl => impl?.SourceLocation?.SourceFile is not (null or ""))
            .Select(impl => impl!.SourceLocation!.WithIdentifierLocation(impl.Method))
            .Select(loc => new LocationOrLocationLink(loc.ToLspLocation()))
            .ToArray();

        if (locations.Length == 0)
        {
            _logger.LogVerbose(
                $"DefinitionHandler: step at offset {offset} in {uri} has no binding locations (undefined/ambiguous)");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        _logger.LogVerbose(
            $"DefinitionHandler: {locations.Length} location(s) for step at offset {offset} in {uri}");

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(locations));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
