using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Handles the standard <c>textDocument/codeLens</c> request for <c>.feature</c> files
/// (hook-match count CodeLens — issue #269). Returns one lens per <c>Feature:</c>/<c>Scenario:</c>/
/// step line that has at least one applicable hook, showing how many would fire for that specific
/// item. Clicking the lens invokes the same <c>reqnroll.goToHooks</c> client command "Go to Hooks"
/// already uses, at that line's position — reusing its existing navigation/disambiguation-picker
/// behavior rather than duplicating it.
/// </summary>
/// <remarks>
/// Applicability/matching is delegated entirely to <see cref="HookMatching"/> — the same helper
/// <c>GoToHooksHandler</c> uses — so this lens's count can never disagree with what clicking it
/// (or invoking Go to Hooks directly) actually shows.
/// </remarks>
public sealed class HookCodeLensHandler
{
    private readonly IDocumentBufferService        _bufferService;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="HookCodeLensHandler"/> class.</summary>
    public HookCodeLensHandler(
        IDocumentBufferService        bufferService,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        IOperationDurationRecorder?   recorder = null)
    {
        _bufferService  = bufferService;
        _registryLookup = registryLookup;
        _logger         = logger;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>
    /// Handles a <c>textDocument/codeLens</c> request.
    /// Returns one lens per Feature:/Scenario:/step line with at least one applicable hook.
    /// Returns <see langword="null"/> for non-.feature files (falls through to the C# step-usage lens).
    /// Returns an empty array when there's no buffer/tags/hooks to work with yet.
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]> HandleAsync(
        CodeLensParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentCodeLens, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"HookCodeLensHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(Empty);
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null
            || buffer.Tags is null || buffer.Tags.Count == 0)
        {
            _logger.LogVerbose($"HookCodeLensHandler: no document buffer/tags for {uri}");
            return Task.FromResult(Empty);
        }

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (ReferenceEquals(registry, ProjectBindingRegistry.Invalid) || registry.Hooks.Length == 0)
        {
            _logger.LogVerbose($"HookCodeLensHandler: no registry or no hooks for {uri}");
            return Task.FromResult(Empty);
        }

        var lenses = new List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();
        // One lens per line: a ScenarioDefinitionBlock and its first StepBlock could otherwise
        // both resolve to the same start line for a step immediately following "Scenario:" with
        // no blank line, so guard against emitting two lenses on one line.
        var seenLines = new HashSet<int>();

        foreach (var tag in buffer.Tags)
        {
            if (tag.Type != DeveroomTagTypes.FeatureBlock
                && tag.Type != DeveroomTagTypes.ScenarioDefinitionBlock
                && tag.Type != DeveroomTagTypes.StepBlock)
                continue;

            var (level, contextTag) = HookMatching.ResolveContext(buffer.Tags, tag.Range.Start);
            if (level == HookContextLevel.None)
                continue;

            var (line, character) = tag.Range.StartLinePosition;
            if (!seenLines.Add(line))
                continue;

            var hooks = HookMatching.ResolveMatchingHooks(registry, level, contextTag);
            if (hooks.Count == 0)
                continue;

            lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
            {
                Range = new LspRange(new Position(line, character), new Position(line, character)),
                Command = new Command
                {
                    Title     = hooks.Count == 1 ? "1 hook" : $"{hooks.Count} hooks",
                    Name      = "reqnroll.goToHooks",
                    Arguments = new JArray(uri.ToString(), line, character),
                },
            });
        }

        _logger.LogVerbose($"HookCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult(lenses.ToArray());
    }

    private static readonly global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[] Empty =
        Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
