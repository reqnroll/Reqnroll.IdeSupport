using System.Linq;
using Gherkin.Ast;
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
/// (hook-match count CodeLens — issue #269). Returns one lens per <c>Feature:</c>/<c>Scenario:</c>
/// line that has at least one hook native to that level (Feature-only / Scenario-only), plus a
/// second lens on the <c>Scenario:</c> line for the step-level hooks shared by every step in that
/// scenario — steps never get their own lens, since <see cref="HookMatching"/> resolves the same
/// step-level hook set for every step in a scenario (matching only depends on the scenario's
/// scope/tags, not on which step), so repeating the count on every step line would be redundant.
/// <c>Background:</c>/Rule sections never get a lens (see the Background check in
/// <see cref="HandleAsync"/> and the RuleBlock remark there) — hook scope is scenario-tag-driven,
/// and neither a Background nor a Rule carries scenario tags of its own.
/// Clicking a lens invokes the <c>reqnroll.goToHooks</c> client command with <c>ownLevelOnly</c>
/// set, so the picker it opens matches exactly what the lens counted.
/// </summary>
/// <remarks>
/// Applicability/matching is delegated entirely to <see cref="HookMatching"/> — the same helper
/// <c>GoToHooksHandler</c> uses — so each lens's count can never disagree with what clicking it
/// actually shows (both use <see cref="HookMatching.GetOwnLevelHookTypes"/> for CodeLens-sourced
/// requests, vs. the cumulative set for a manual "Go to Hooks" invocation from the cursor).
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
    /// Returns one lens per Feature:/Scenario: line with at least one own-level hook, plus a
    /// second lens on the Scenario: line for the scenario's step-level hooks (see class remarks).
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

        foreach (var tag in buffer.Tags)
        {
            if (tag.Type == IdeSupportTagTypes.FeatureBlock)
            {
                AddOwnLevelLens(lenses, uri, registry, HookContextLevel.Feature, tag, clickTargetTag: tag);
            }
            else if (tag.Type == IdeSupportTagTypes.ScenarioDefinitionBlock && tag.Data is not Background)
            {
                // IdeSupportTagTypes.ScenarioDefinitionBlock covers Background blocks too (see
                // its doc comment), but a Background has no tags/scope of its own — which hooks
                // apply to its steps depends entirely on whichever Scenario is currently pulling
                // them in, so there is nothing correct to count or navigate to here. Excluded via
                // tag.Data (the underlying Gherkin.Ast node) rather than a Type check, since
                // Background and Scenario share the same tag Type. Rule blocks need no equivalent
                // check: HookContextLevel/HookType have no Rule-level concept at all, so
                // IdeSupportTagTypes.RuleBlock is never matched by the `if`/`else if` above.
                AddOwnLevelLens(lenses, uri, registry, HookContextLevel.Scenario, tag, clickTargetTag: tag);
                AddStepHooksLens(lenses, uri, registry, tag, buffer.Tags);
            }
        }

        _logger.LogVerbose($"HookCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult(lenses.ToArray());
    }

    /// <summary>
    /// Adds a lens displayed at <paramref name="displayTag"/>'s line, counting only hooks native
    /// to <paramref name="level"/> (see <see cref="HookMatching.GetOwnLevelHookTypes"/>). The
    /// click target (<paramref name="clickTargetTag"/>) resolves to the same level so
    /// <c>GoToHooksHandler</c>, given <c>ownLevelOnly</c>, shows exactly this count.
    /// </summary>
    private static void AddOwnLevelLens(
        List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> lenses,
        DocumentUri            uri,
        ProjectBindingRegistry registry,
        HookContextLevel       level,
        IdeSupportTag            displayTag,
        IdeSupportTag            clickTargetTag)
    {
        var hooks = HookMatching.ResolveMatchingHooks(registry, level, displayTag, ownLevelOnly: true);
        if (hooks.Count == 0)
            return;

        var (displayLine, displayChar) = displayTag.Range.StartLinePosition;
        var (clickLine, clickChar)     = clickTargetTag.Range.StartLinePosition;

        lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(displayLine, displayChar), new Position(displayLine, displayChar)),
            Command = new Command
            {
                Title     = hooks.Count == 1 ? "1 hook" : $"{hooks.Count} hooks",
                Name      = "reqnroll.goToHooks",
                Arguments = new JArray(uri.ToString(), clickLine, clickChar, true),
            },
        });
    }

    /// <summary>
    /// Adds a second lens on the <c>Scenario:</c> line for the step-level hooks (Before/AfterStep,
    /// Before/AfterScenarioBlock) shared by every step in <paramref name="scenarioTag"/> — skipped
    /// when the scenario has no steps yet, since there is no line to navigate to on click. Carries
    /// a 5th <c>true</c> argument (absent on <see cref="AddOwnLevelLens"/>'s lenses) so a client
    /// that must render this lens via a separate CodeVision-style provider registration (Rider —
    /// see HookCodeVisionProvider.kt/StepHooksCodeVisionProvider.kt) can tell the two lens kinds
    /// apart reliably, rather than parsing the title text. VS Code doesn't need this: it renders
    /// every lens from a single generic CodeLens provider and stacks same-range entries itself.
    /// </summary>
    private static void AddStepHooksLens(
        List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> lenses,
        DocumentUri              uri,
        ProjectBindingRegistry   registry,
        IdeSupportTag              scenarioTag,
        IReadOnlyCollection<IdeSupportTag> allTags)
    {
        var firstStepTag = allTags
            .Where(t => t.Type == IdeSupportTagTypes.StepBlock
                     && t.Range.Start >= scenarioTag.Range.Start && t.Range.Start < scenarioTag.Range.End)
            .OrderBy(t => t.Range.Start)
            .FirstOrDefault();
        if (firstStepTag is null)
            return;

        var hooks = HookMatching.ResolveMatchingHooks(registry, HookContextLevel.Step, scenarioTag, ownLevelOnly: true);
        if (hooks.Count == 0)
            return;

        var (displayLine, displayChar) = scenarioTag.Range.StartLinePosition;
        var (stepLine, stepChar)       = firstStepTag.Range.StartLinePosition;

        lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(displayLine, displayChar), new Position(displayLine, displayChar)),
            Command = new Command
            {
                Title     = hooks.Count == 1 ? "1 step hook" : $"{hooks.Count} step hooks",
                Name      = "reqnroll.goToHooks",
                Arguments = new JArray(uri.ToString(), stepLine, stepChar, true, true),
            },
        });
    }

    private static readonly global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[] Empty =
        Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
