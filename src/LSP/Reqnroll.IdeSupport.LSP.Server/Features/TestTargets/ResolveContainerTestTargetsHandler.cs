using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>
/// Handles the custom <c>reqnroll/resolveContainerTestTargets</c> request (issue #744, "Run
/// scenarios" on a <c>Feature:</c> or <c>Rule:</c> block). Given the full body range of a Feature or
/// Rule in a <c>.feature</c> file, resolves the generated C# test method(s) for every
/// Scenario/Scenario Outline it contains, by delegating to
/// <see cref="IScenarioTestTargetResolver.ResolveAll"/>. Structurally a sibling of
/// <see cref="ResolveTestTargetsHandler"/> (same guard rails, same DTO shape) rather than an
/// extension of it, so the well-exercised single-scenario path is untouched by this broader query.
/// </summary>
public sealed class ResolveContainerTestTargetsHandler
{
    private readonly IDocumentBufferService _bufferService;
    private readonly IScenarioTestTargetResolver _resolver;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="ResolveContainerTestTargetsHandler"/> class.</summary>
    public ResolveContainerTestTargetsHandler(
        IDocumentBufferService bufferService,
        IScenarioTestTargetResolver resolver,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _bufferService = bufferService;
        _resolver = resolver;
        _scopeManager = scopeManager;
        _logger = logger;
        _telemetryService = telemetryService;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/resolveContainerTestTargets</c> request.</summary>
    public Task<ResolveContainerTestTargetsResponse> HandleAsync(
        ResolveContainerTestTargetsParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollResolveContainerTestTargets, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"ResolveContainerTestTargetsHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new ResolveContainerTestTargetsResponse());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"ResolveContainerTestTargetsHandler: no document buffer for {uri}");
            return Task.FromResult(new ResolveContainerTestTargetsResponse());
        }

        if (buffer.Tags is null || buffer.Tags.Count == 0)
        {
            _logger.LogVerbose($"ResolveContainerTestTargetsHandler: tags not yet computed for {uri}");
            return Task.FromResult(new ResolveContainerTestTargetsResponse());
        }

        // Same snapshot-anchoring rationale as ResolveTestTargetsHandler: GherkinRange.IntersectsWith
        // (and, indirectly here, plain offset comparisons against buffer.Tags' own ranges) requires
        // both ranges to reference the same snapshot instance as the tags they're compared against.
        var snapshot = buffer.Tags.First().Range.Snapshot;
        var startOffset = snapshot.ToOffset(request.Range.Start.Line, request.Range.Start.Character);
        var endOffset = snapshot.ToOffset(request.Range.End.Line, request.Range.End.Character);
        var containerRange = Core.Documents.GherkinRange.FromPoint(snapshot, startOffset, Math.Max(0, endOffset - startOffset));

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
        {
            _logger.LogVerbose($"ResolveContainerTestTargetsHandler: could not resolve a local path for {uri}");
            return Task.FromResult(new ResolveContainerTestTargetsResponse());
        }

        var projectFolder = _scopeManager.ResolvePrimaryOwner(uri)?.ProjectFolder;

        var targets = _resolver.ResolveAll(new Uri(filePath), buffer.Tags, containerRange, projectFolder);

        _logger.LogVerbose($"ResolveContainerTestTargetsHandler: {targets.Count} target(s) in container range {request.Range} in {uri}");

        _telemetryService?.SendEvent(TelemetryEvents.ResolveContainerTestTargetsCommandExecuted, new());

        return Task.FromResult(new ResolveContainerTestTargetsResponse { Targets = targets.Select(ToDto).ToList() });
    }

    private static ScenarioTestTargetDto ToDto(Core.TestTargets.ScenarioTestTarget target) => new()
    {
        DeclaringTypeFullName = target.DeclaringTypeFullName,
        MethodName = target.MethodName,
        IsParameterized = target.IsParameterized,
        RowArguments = target.RowArguments is null ? null : new Dictionary<string, string>(target.RowArguments),
        RowIndex = target.RowIndex,
    };

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
