#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles the custom <c>reqnroll/findStepDefinitions</c> request (issue #757): the step-definition
/// bindings matching the step at a <c>.feature</c> caret position, with the per-binding detail a
/// results list needs — class, method, binding expression and whether the source is on this machine.
/// </summary>
/// <remarks>
/// Same bindings as <c>textDocument/definition</c> (both go through <see cref="StepAtPositionResolver"/>),
/// but the standard response only carries <c>Location</c>s, which left the Visual Studio extension
/// showing each binding's declaration line read back from disk — stale for an unsaved edit, and
/// silent about why a step is ambiguous. The binding expression is what answers that.
/// <para>
/// Unlike <c>textDocument/definition</c>, bindings whose source file is not on this machine are
/// included (with <see cref="StepDefinitionItem.IsResolved"/> false) so the client can say why a row
/// cannot be navigated rather than silently leaving it out (issue #540).
/// </para>
/// </remarks>
public sealed class FindStepDefinitionsHandler
{
    private readonly StepAtPositionResolver     _resolver;
    private readonly IIdeSupportLogger          _logger;
    private readonly IFileSystemForIDE          _fileSystem;
    private readonly ILspTelemetryService?      _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="FindStepDefinitionsHandler"/> class.</summary>
    public FindStepDefinitionsHandler(
        IBindingMatchService        matchService,
        IDocumentBufferService      bufferService,
        ILspWorkspaceScopeManager   scopeManager,
        IIdeSupportLogger           logger,
        IFileSystemForIDE           fileSystem,
        ILspTelemetryService?       telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _resolver         = new StepAtPositionResolver(matchService, bufferService, scopeManager, logger, nameof(FindStepDefinitionsHandler));
        _logger           = logger;
        _fileSystem       = fileSystem;
        _telemetryService = telemetryService;
        _recorder         = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/findStepDefinitions</c> request.</summary>
    public Task<FindStepDefinitionsResponse> HandleAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollFindStepDefinitions, uri);

        var step = _resolver.FindStep(uri, request.Position);
        if (step is null)
            return Task.FromResult(new FindStepDefinitionsResponse());

        var items = _resolver.GetBindingsWithSource(step).Select(ToItem).ToList();
        var resolvedCount = items.Count(i => i.IsResolved);

        // Same shape as DefinitionHandler's/GoToHooksHandler's result line, so the server log alone
        // answers "what did Go to Step Definition find" (Rider and VS log no client-side trace of it).
        _logger.LogVerbose(
            $"FindStepDefinitionsHandler: {items.Count} step definition(s) ({resolvedCount} navigable) for step at " +
            $"{request.Position.Line}:{request.Position.Character} in {uri}");

        // Same event and property as textDocument/definition: this is the same user command, reached
        // through the request Visual Studio sends for it. LocationCount counts navigable rows.
        _telemetryService?.SendEvent(DefinitionHandler.TelemetryEventName, new()
        {
            ["LocationCount"] = resolvedCount,
        });

        return Task.FromResult(new FindStepDefinitionsResponse { Items = items });
    }

    private StepDefinitionItem ToItem(ProjectStepDefinitionBinding binding)
    {
        var impl = binding.Implementation;
        var (className, methodName) = FindUnusedStepDefinitionsService.ParseMethod(impl.Method);
        var loc = impl.SourceLocation!.IsResolved
            ? impl.SourceLocation.WithIdentifierLocation(impl.Method, _fileSystem)
            : impl.SourceLocation;

        return new StepDefinitionItem
        {
            // Left unset: the binding model does not record which project declares a binding, and
            // the feature's own project would mislabel one from a referenced assembly.
            ProjectName        = null,
            ClassName          = className,
            MethodName         = methodName,
            BindingExpression  = binding.DisplayExpression,
            StepDefinitionType = StepDefinitionItem.ToWireType(binding.StepDefinitionType),
            SourceFile         = loc.IsResolved ? loc.SourceFile : null,
            SourceLine         = loc.SourceFileLine - 1,     // 1-based → 0-based
            SourceChar         = loc.SourceFileColumn - 1,   // 1-based → 0-based
            IsResolved         = loc.IsResolved,
            RecordedSourceFile = loc.IsResolved && PathUtils.IsSamePath(loc.SourceFile, loc.RecordedSourceFile)
                ? null
                : loc.RecordedSourceFile,
        };
    }
}
