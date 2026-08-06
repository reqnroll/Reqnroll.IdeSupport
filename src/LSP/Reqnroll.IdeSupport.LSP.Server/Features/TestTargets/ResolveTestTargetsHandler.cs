using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>
/// Handles the custom <c>reqnroll/resolveTestTargets</c> request (design doc §4). Given a range in
/// a <c>.feature</c> file, resolves the generated C# test method(s) that the scenario/Outline/
/// example row at that range corresponds to, by delegating to <see cref="IScenarioTestTargetResolver"/>.
/// </summary>
public sealed class ResolveTestTargetsHandler
{
    private readonly IDocumentBufferService _bufferService;
    private readonly IScenarioTestTargetResolver _resolver;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger _logger;
    private readonly ILspTelemetryService? _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="ResolveTestTargetsHandler"/> class.</summary>
    public ResolveTestTargetsHandler(
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

    /// <summary>Handles a <c>reqnroll/resolveTestTargets</c> request.</summary>
    public Task<ResolveTestTargetsResponse> HandleAsync(
        ResolveTestTargetsParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollResolveTestTargets, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"ResolveTestTargetsHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new ResolveTestTargetsResponse());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"ResolveTestTargetsHandler: no document buffer for {uri}");
            return Task.FromResult(new ResolveTestTargetsResponse());
        }

        if (buffer.Tags is null || buffer.Tags.Count == 0)
        {
            _logger.LogVerbose($"ResolveTestTargetsHandler: tags not yet computed for {uri}");
            return Task.FromResult(new ResolveTestTargetsResponse());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var startOffset = snapshot.ToOffset(request.Range.Start.Line, request.Range.Start.Character);
        var endOffset = snapshot.ToOffset(request.Range.End.Line, request.Range.End.Character);
        var scenarioRange = Core.Documents.GherkinRange.FromPoint(snapshot, startOffset, Math.Max(0, endOffset - startOffset));

        var packageIds = _scopeManager.ResolveOwners(uri)
            .SelectMany(p => p.PackageReferences)
            .Select(p => p.PackageName)
            .Distinct()
            .ToArray();

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
        {
            _logger.LogVerbose($"ResolveTestTargetsHandler: could not resolve a local path for {uri}");
            return Task.FromResult(new ResolveTestTargetsResponse());
        }

        var projectFolder = _scopeManager.ResolvePrimaryOwner(uri)?.ProjectFolder;

        var targets = _resolver.Resolve(new Uri(filePath), buffer.Tags, scenarioRange, packageIds, projectFolder);

        _logger.LogVerbose($"ResolveTestTargetsHandler: {targets.Count} target(s) at range {request.Range} in {uri}");

        _telemetryService?.SendEvent("ResolveTestTargets command executed", new());

        return Task.FromResult(new ResolveTestTargetsResponse { Targets = targets.Select(ToDto).ToList() });
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
