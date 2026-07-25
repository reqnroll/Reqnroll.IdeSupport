using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles the custom <c>reqnroll/goToStepDefinitions</c> request (Go to Step Definition).
/// <para>
/// Returns all step-definition bindings that match the step at the queried cursor position, each
/// with its source location <b>and</b> metadata (step keyword type, qualified method name).  This
/// richer payload lets the VS extension's picker display labels such as
/// "[When] CalculatorSteps.AddNumbers (Steps.cs:18)" rather than just "Steps.cs:18".
/// </para>
/// <para>
/// The standard <c>textDocument/definition</c> handler (<see cref="DefinitionHandler"/>)
/// is retained for generic LSP clients that do not understand this custom message.
/// </para>
/// </summary>
public sealed class GoToStepDefinitionsHandler
{
    private readonly IBindingMatchService      _matchService;
    private readonly IDocumentBufferService    _bufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger           _logger;
    private readonly ILspTelemetryService?     _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="GoToStepDefinitionsHandler"/> class.</summary>
    public GoToStepDefinitionsHandler(
        IBindingMatchService      matchService,
        IDocumentBufferService    bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger           logger,
        ILspTelemetryService?     telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService  = matchService;
        _bufferService = bufferService;
        _scopeManager  = scopeManager;
        _logger        = logger;
        _telemetryService = telemetryService;
        _recorder      = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/goToStepDefinitions</c> request for step-definition navigation.</summary>
    public Task<GoToStepDefinitionsResponse> HandleAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): same latency class as textDocument/definition,
        // recorded separately since this is a distinct custom request.
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollGoToStepDefinitions, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"GoToStepDefinitionsHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new GoToStepDefinitionsResponse());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"GoToStepDefinitionsHandler: no document buffer for {uri}");
            return Task.FromResult(new GoToStepDefinitionsResponse());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(request.Position.Line, request.Position.Character);

        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        var docId = uri.ToString();
        if (!_matchService.TryGet(new MatchSetKey(docId, owner), out var matchSet) || matchSet is null)
        {
            _logger.LogVerbose($"GoToStepDefinitionsHandler: no match set cached for {uri}");
            return Task.FromResult(new GoToStepDefinitionsResponse());
        }

        var step = matchSet.FindAt(offset);
        if (step is null)
        {
            _logger.LogVerbose($"GoToStepDefinitionsHandler: no step at offset {offset} in {uri}");
            return Task.FromResult(new GoToStepDefinitionsResponse());
        }

        var response = new GoToStepDefinitionsResponse();
        foreach (var item in step.Result.Items)
        {
            var binding = item.MatchedStepDefinition;
            var src     = binding?.Implementation?.SourceLocation;
            if (src is null || string.IsNullOrEmpty(src.SourceFile))
                continue;

            var identSrc = src.WithIdentifierLocation(binding!.Implementation?.Method);
            response.StepDefinitions.Add(new GoToStepDefinitionLocation
            {
                Uri        = DocumentUri.FromFileSystemPath(identSrc.SourceFile).ToString(),
                StartLine  = identSrc.SourceFileLine   - 1,
                StartChar  = identSrc.SourceFileColumn - 1,
                StepType   = binding.StepDefinitionType.ToString(),
                MethodName = binding.Implementation?.Method ?? string.Empty,
            });
        }

        _logger.LogVerbose(
            $"GoToStepDefinitionsHandler: {response.StepDefinitions.Count} binding(s) for step at offset {offset} in {uri}");

        // Telemetry
        _telemetryService?.SendEvent("GoToStepDefinition command executed", new()
        {
            ["GenerateSnippet"] = step.Result.Items.Any(i => i.Type == MatchResultType.Undefined),
        });

        return Task.FromResult(response);
    }

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
