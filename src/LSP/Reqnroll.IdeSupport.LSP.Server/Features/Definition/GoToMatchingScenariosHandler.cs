using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles the custom <c>reqnroll/goToMatchingScenarios</c> request (issue #373's hook-match-count
/// CodeLens click action).
/// <para>
/// Given a position in a <c>.cs</c> file pointing at a hook-binding method (the exact attribute
/// location <see cref="Features.CodeLens.HookMatchCountCodeLensHandler"/> already computed and
/// echoed back as the lens's click arguments), resolves the hook binding at that position and
/// returns every scenario, across the whole owning project(s), that its scope matches.
/// </para>
/// <para>
/// The inverse of <see cref="GoToHooksHandler"/> ("given a <c>.feature</c> position, which hooks
/// apply") — both delegate their actual scope-matching to shared <c>LSP.Core.Matching</c> helpers
/// (<see cref="HookScenarioMatching"/> here, <c>HookMatching</c> there) so the two directions can
/// never disagree about what "applicable"/"matches" means.
/// </para>
/// </summary>
public sealed class GoToMatchingScenariosHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="GoToMatchingScenariosHandler"/> class.</summary>
    public GoToMatchingScenariosHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        ILspTelemetryService?         telemetryService = null,
        IOperationDurationRecorder?   recorder = null)
    {
        _matchService   = matchService;
        _scopeManager   = scopeManager;
        _registryLookup = registryLookup;
        _logger         = logger;
        _telemetryService = telemetryService;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/goToMatchingScenarios</c> request.</summary>
    public Task<GoToMatchingScenariosResponse> HandleAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollGoToMatchingScenarios, uri);

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"GoToMatchingScenariosHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult(new GoToMatchingScenariosResponse());
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult(new GoToMatchingScenariosResponse());

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (ReferenceEquals(registry, ProjectBindingRegistry.Invalid))
        {
            _logger.LogVerbose($"GoToMatchingScenariosHandler: no binding registry available for {uri}");
            return Task.FromResult(new GoToMatchingScenariosResponse());
        }

        // LSP positions are 0-based; SourceLocation is 1-based.
        var line = request.Position.Line + 1;
        var col  = request.Position.Character + 1;

        // Exact match, not a lookback/proximity search like GoToHooksHandler's cursor resolution:
        // the position we're given is always the lens's own attribute location, round-tripped
        // verbatim from HookMatchCountCodeLensHandler's response arguments.
        var hook = registry.Hooks.FirstOrDefault(h =>
            h.IsValid &&
            h.Implementation?.SourceLocation is { } src &&
            IsSameFile(src.SourceFile, filePath) &&
            src.SourceFileLine == line &&
            src.SourceFileColumn == col);

        if (hook is null)
        {
            _logger.LogVerbose($"GoToMatchingScenariosHandler: no hook binding at {filePath}:{line}:{col}");
            return Task.FromResult(new GoToMatchingScenariosResponse());
        }

        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var matchSets = _matchService.GetAll(projectFilter);
        var scenarios = HookScenarioMatching.ResolveMatchingScenarios(matchSets, hook);

        _logger.LogVerbose(
            $"GoToMatchingScenariosHandler: {scenarios.Count} scenario(s) for hook at {filePath}:{line}:{col}");

        var locations = scenarios.Select(ToLocation).ToList();

        _telemetryService?.SendEvent("GoToMatchingScenarios command executed", new());

        return Task.FromResult(new GoToMatchingScenariosResponse { Scenarios = locations });
    }

    private static MatchingScenarioLocation ToLocation(FeatureScenarioInfo scenario)
    {
        var (line, character) = scenario.Range.StartLinePosition;
        return new MatchingScenarioLocation
        {
            Uri          = scenario.FeatureDocumentId,
            StartLine    = line,
            StartChar    = character,
            ScenarioName = scenario.Name ?? "",
            IsOutline    = scenario.IsOutline,
        };
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
