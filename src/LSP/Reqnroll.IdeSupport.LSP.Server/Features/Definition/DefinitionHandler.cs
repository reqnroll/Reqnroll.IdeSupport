#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.Common.Lsp;

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
    private readonly StepAtPositionResolver    _resolver;
    private readonly IIdeSupportLogger           _logger;
    private readonly ILspTelemetryService?      _telemetryService;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IFileSystemForIDE         _fileSystem;
    private readonly ClientIdeContext?         _clientIde;

    /// <summary>Initializes a new instance of the <see cref="DefinitionHandler"/> class.</summary>
    public DefinitionHandler(
        IBindingMatchService      matchService,
        IDocumentBufferService    bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger           logger,
        IFileSystemForIDE         fileSystem,
        ILspTelemetryService?     telemetryService = null,
        IOperationDurationRecorder? recorder = null,
        ClientIdeContext?         clientIde = null)
    {
        _resolver      = new StepAtPositionResolver(matchService, bufferService, scopeManager, logger, nameof(DefinitionHandler));
        _logger        = logger;
        _fileSystem    = fileSystem;
        _telemetryService = telemetryService;
        _recorder      = recorder ?? NullOperationDurationRecorder.Instance;
        _clientIde     = clientIde;
    }

    /// <summary>Builds the LSP registration options advertising go-to-definition support for <c>.feature</c> files.</summary>
    public DefinitionRegistrationOptions GetRegistrationOptions(
        DefinitionCapability    capability,
        ClientCapabilities      clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = DocumentGlobPatterns.FeatureFilePattern })
        };

    /// <summary>Handles a <c>textDocument/definition</c> request for step-definition navigation.</summary>
    public Task<LocationOrLocationLinks?> Handle(
        DefinitionParams  request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): time the cache-hit definition round-trip (the handler's own work).
        using var _perf = _recorder.Measure(LspStandardMethodNames.TextDocumentDefinition, uri);

        var step = _resolver.FindStep(uri, request.Position);
        if (step is null)
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());

        // A binding whose source file could not be found on this machine has no navigation target
        // here, and emitting one anyway is the issue #540 incident: the URI is well-formed, the IDE
        // accepts it, and Go To Definition silently does nothing. Drop it (the resolver logs why).
        var locations = _resolver.GetBindingsWithSource(step)
            .Select(sd => sd.Implementation)
            .Where(impl => impl.SourceLocation!.IsResolved)
            .Select(impl => impl.SourceLocation!.WithIdentifierLocation(impl.Method, _fileSystem))
            .Select(loc => new LocationOrLocationLink(loc.ToLspLocation()))
            .ToArray();

        // Telemetry: a *lookup*, not a navigation (issue #899). VS Code and Rider send this request on hover
        // as well as for F12/Ctrl+click and the client cannot tell the two apart, so it is the same lookup
        // event FindStepDefinitionsHandler sends. A client that navigates through reqnroll/findStepDefinitions
        // (Visual Studio) only reaches here as its own provider's fall-through on hover: nothing to count.
        // LocationCount is 0 for the undefined/ambiguous/unresolved cases below.
        if (_clientIde?.Behaviours.NavigatesStepsViaFindStepDefinitions != true)
        {
            _telemetryService?.SendEvent(TelemetryEvents.FindStepDefinitionsCommandExecuted, new()
            {
                ["LocationCount"] = locations.Length,
                [TelemetryProperties.Status] = StepAtPositionResolver.ClassifyStatus(step, locations.Length),
                [TelemetryProperties.Protocol] = LspStandardMethodNames.TextDocumentDefinition,
            });
        }

        if (locations.Length == 0)
        {
            _logger.LogVerbose(
                $"DefinitionHandler: step at {request.Position.Line}:{request.Position.Character} in {uri} has no binding locations (undefined/ambiguous/unresolved)");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        _logger.LogVerbose(
            $"DefinitionHandler: {locations.Length} location(s) for step at {request.Position.Line}:{request.Position.Character} in {uri}");

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(locations));
    }
}
