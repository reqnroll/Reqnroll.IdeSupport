using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Handles the custom <c>reqnroll/goToHooks</c> request (Hook Navigation — "Go to Hooks").
/// <para>
/// Given a cursor position in a <c>.feature</c> file, returns all hook bindings that are
/// applicable at that position, filtered by context level (Feature / Scenario / Step) and
/// any tag/scope expressions on the hook.
/// </para>
/// <para>
/// A separate custom message is used rather than reusing <c>textDocument/definition</c>
/// because that message is already used by Go to Step Definition on step lines;
/// the server cannot distinguish the two intents from position alone, and step-level hooks
/// (<c>[BeforeStep]</c> / <c>[AfterStep]</c>) would be unreachable.
/// </para>
/// </summary>
public sealed class GoToHooksHandler
{
    private readonly IDocumentBufferService        _bufferService;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?          _telemetryService;
    private readonly IOperationDurationRecorder     _recorder;

    /// <summary>Initializes a new instance of the <see cref="GoToHooksHandler"/> class.</summary>
    public GoToHooksHandler(
        IDocumentBufferService        bufferService,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        ILspTelemetryService?         telemetryService = null,
        IOperationDurationRecorder?   recorder = null)
    {
        _bufferService  = bufferService;
        _registryLookup = registryLookup;
        _logger         = logger;
        _telemetryService = telemetryService;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/goToHooks</c> request for hook navigation.</summary>
    public Task<GoToHooksResponse> HandleAsync(
        TextDocumentPositionParams request,
        CancellationToken          cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): same latency class as textDocument/definition.
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollGoToHooks, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"GoToHooksHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"GoToHooksHandler: no document buffer for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        if (buffer.Tags is null || buffer.Tags.Count == 0)
        {
            _logger.LogVerbose($"GoToHooksHandler: tags not yet computed for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(request.Position.Line, request.Position.Character);

        var (level, contextTag) = HookMatching.ResolveContext(buffer.Tags, offset);
        if (level == HookContextLevel.None)
        {
            _logger.LogVerbose($"GoToHooksHandler: no Gherkin context at offset {offset} in {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (ReferenceEquals(registry, ProjectBindingRegistry.Invalid))
        {
            _logger.LogVerbose($"GoToHooksHandler: no binding registry available for {uri}");
            return Task.FromResult(new GoToHooksResponse());
        }

        var hooks = HookMatching.ResolveMatchingHooks(registry, level, contextTag);

        _logger.LogVerbose($"GoToHooksHandler: {hooks.Count} hook(s) at offset {offset} in {uri}");

        var locations = new List<GoToHookLocation>(hooks.Count);
        foreach (var hook in hooks)
        {
            var loc = ToLocation(hook);
            if (loc is not null)
                locations.Add(loc);
        }

        // Telemetry
        _telemetryService?.SendEvent("GoToHook command executed", new());

        return Task.FromResult(new GoToHooksResponse { Hooks = locations });
    }

    // ── Location conversion ───────────────────────────────────────────────────

    private static GoToHookLocation? ToLocation(ProjectHookBinding hook)
    {
        var src = hook.Implementation?.SourceLocation;
        if (src is null || string.IsNullOrEmpty(src.SourceFile))
            return null;

        // SourceLocation is 1-based; response uses 0-based (LSP convention).
        return new GoToHookLocation
        {
            Uri        = DocumentUri.FromFileSystemPath(src.SourceFile).ToString(),
            StartLine  = src.SourceFileLine   - 1,
            StartChar  = src.SourceFileColumn - 1,
            HookType   = hook.HookType.ToString(),
            HookOrder  = hook.HookOrder,
            MethodName = hook.Implementation?.Method ?? string.Empty,
        };
    }

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
