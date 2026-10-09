using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;

namespace Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;

/// <summary>
/// Handles the custom <c>reqnroll/findUnusedStepDefinitions</c> request (Find Unused Step
/// Definitions). Resolves all
/// project binding registries and delegates the scan/dedupe/match algorithm to
/// <see cref="IFindUnusedStepDefinitionsService"/> (LSP.Core), then maps the result to the wire
/// <see cref="FindUnusedStepDefinitionsResponse"/> shape and fires telemetry.
/// </summary>
public sealed class FindUnusedStepDefinitionsHandler
{
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IFindUnusedStepDefinitionsService _service;
    private readonly ILspTelemetryService? _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="FindUnusedStepDefinitionsHandler"/> class.</summary>
    public FindUnusedStepDefinitionsHandler(
        IProjectBindingRegistryLookup registryLookup,
        IFindUnusedStepDefinitionsService service,
        ILspTelemetryService? telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _registryLookup = registryLookup;
        _service = service;
        _telemetryService = telemetryService;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/findUnusedStepDefinitions</c> request.</summary>
    public Task<FindUnusedStepDefinitionsResponse> HandleAsync(CancellationToken cancellationToken)
    {
        // Performance Verification (Layer 4): time the full-workspace unused-step-definitions scan —
        // the operation shape most likely to regress silently on large solutions.
        using var _perf = _recorder.Measure(CustomLspMethodNames.ReqnrollFindUnusedStepDefinitions);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var allRegistries = _registryLookup.GetAllRegistries();

        // Owner.ProjectFile is the full .csproj path; its directory is what "this project's own
        // folder" means for FindUnusedStepDefinitionsService's ownership attribution (issue #547).
        var unused = _service.FindUnusedStepDefinitions(
            allRegistries
                .Select(r => (r.ProjectName, Path.GetDirectoryName(r.Owner.ProjectFile) ?? string.Empty, r.Registry))
                .ToList());

        var items = unused.Select(u => new StepDefinitionItem
        {
            ProjectName = u.ProjectName,
            ClassName = u.ClassName,
            MethodName = u.MethodName,
            BindingExpression = u.BindingExpression,
            SourceFile = u.SourceFile,
            SourceLine = u.SourceLine - 1,     // 1-based → 0-based
            SourceChar = u.SourceColumn - 1,   // 1-based → 0-based
            IsResolved = u.IsResolved,
            RecordedSourceFile = u.RecordedSourceFile,
            StepDefinitionType = StepDefinitionItem.ToWireType(u.StepDefinitionType),
        }).ToList();

        _telemetryService?.SendEvent(TelemetryEvents.FindUnusedStepDefinitionsCommandExecuted, new()
        {
            ["UnusedStepDefinitions"] = items.Count,
            [TelemetryProperties.ScannedProjects] = allRegistries.Count,
            ["IsCancellationRequested"] = cancellationToken.IsCancellationRequested,
            [TelemetryProperties.TotalStepDefinitions] = CountScannedStepDefinitions(allRegistries.Select(r => r.Registry)),
            [TelemetryProperties.DurationBucket] = TelemetryBuckets.DurationSince(started),
        });

        return Task.FromResult(new FindUnusedStepDefinitionsResponse { Items = items });
    }

    /// <summary>
    /// The denominator for the unused ratio: distinct <see cref="BindingId"/>s among the step
    /// definitions <see cref="FindUnusedStepDefinitionsService"/> scans (valid, with a source location),
    /// so a binding reported by several projects counts once, exactly as it does in the unused count.
    /// </summary>
    private static int CountScannedStepDefinitions(IEnumerable<ProjectBindingRegistry> registries) =>
        registries
            .Where(r => r != ProjectBindingRegistry.Invalid)
            .SelectMany(r => r.StepDefinitions)
            .Where(sd => sd.IsValid && sd.Implementation?.SourceLocation is not null)
            .Select(BindingId.For)
            .Distinct()
            .Count();
}
