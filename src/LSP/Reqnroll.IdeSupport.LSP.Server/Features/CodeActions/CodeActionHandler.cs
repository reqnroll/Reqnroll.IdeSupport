using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>textDocument/codeAction</c> requests for <c>*.feature</c> files (Define Steps).
/// Returns code actions that generate C# step-definition stubs for undefined steps.
/// Registered via OmniSharp dynamic registration (<see cref="ICodeActionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
/// <remarks>
/// Reduced to guards, orchestration, and telemetry (issue #588): resolving <em>where</em>
/// generated code should go is <see cref="StepDefinitionTargetResolver"/>, and building the
/// actions for a given target/step-subset is <see cref="DefineStepsActionBuilder"/>. Both are
/// plain internal collaborators constructed here, not DI-registered services — they have no
/// other consumer and no reason to be swapped independently of this handler.
/// </remarks>
public sealed class CodeActionHandler : ICodeActionHandler
{
    /// <summary>
    /// Caps the total number of code actions returned for one request (append candidates + the
    /// new-file fallback, across both the per-step and "all"/"scenario" title groups).
    /// </summary>
    private const int MaxTargetedActions = 6;

    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IDocumentBufferService        _bufferService;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;
    private readonly StepDefinitionTargetResolver  _targetResolver;
    private readonly DefineStepsActionBuilder      _actionBuilder;

    /// <summary>Initializes a new instance of the <see cref="CodeActionHandler"/> class.</summary>
    public CodeActionHandler(
        IBindingMatchService      matchService,
        IStepScaffoldService      scaffoldService,
        ILspWorkspaceScopeManager scopeManager,
        IDocumentBufferService    bufferService,
        IIdeSupportLogger            logger,
        IFileSystemForIDE         fileSystem,
        ILspTelemetryService?     telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _bufferService   = bufferService;
        _logger          = logger;
        _telemetryService = telemetryService;
        _recorder        = recorder ?? NullOperationDurationRecorder.Instance;
        _targetResolver  = new StepDefinitionTargetResolver(scopeManager, fileSystem);
        _actionBuilder   = new DefineStepsActionBuilder(scaffoldService, fileSystem);
    }

    /// <summary>Builds the LSP registration options advertising code-action support (quick-fix kind) for <c>.feature</c> files.</summary>
    public CodeActionRegistrationOptions GetRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities   clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" }),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false
        };

    /// <summary>Handles a <c>textDocument/codeAction</c> request (lightbulb actions).</summary>
    public Task<CommandOrCodeActionContainer?> Handle(
        CodeActionParams    request,
        CancellationToken   cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentCodeAction, uri);

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"CodeActionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer());
        }

        // Resolve the match set for the feature file's primary owner.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var matchKey = primaryOwner is not null
            ? new MatchSetKey(uri.ToString(),
                new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker))
            : MatchSetKey.ForUnknownProject(uri.ToString());

        _matchService.TryGet(matchKey, out var matchSet);

        var allUndefined = matchSet?.Undefined.ToList() ?? new List<LSP.Core.Matching.StepBindingMatch>();
        if (allUndefined.Count == 0)
        {
            _logger.LogVerbose($"CodeActionHandler: no undefined steps for {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer());
        }

        // Only offer "Define missing step" actions when the request's cursor position actually
        // falls on an undefined step. Without this, a lightbulb invoked over an ambiguous (or
        // otherwise bound) step would still offer to "define" some unrelated undefined step
        // elsewhere in the file, which is misleading — that step has nothing to do with what's
        // under the cursor.
        var stepAtCursor = ResolveStepAtCursor(uri, request.Range.Start, matchSet);
        if (stepAtCursor is null || !stepAtCursor.IsUndefined)
        {
            _logger.LogVerbose($"CodeActionHandler: no undefined step at the request position in {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer());
        }

        var featurePath = uri.GetFileSystemPath();
        var target = _targetResolver.Resolve(uri, featurePath, primaryOwner, matchSet);

        // Collect actions. Per-step ("cursor") actions are inserted first so they survive the
        // MaxTargetedActions cap below when both this and the "all"/"scenario" group are present.
        var actions = new List<CommandOrCodeAction>();

        // ── "Define all missing steps in file" ─────────────────────────────────
        if (allUndefined.Count >= 1)
        {
            actions.AddRange(_actionBuilder.Build(target,
                allUndefined.Count == 1 ? "Define missing step" : "Define all missing steps in file",
                allUndefined));
        }

        // ── Per-step action for the step actually under the cursor ─────────────
        // Only add it as a distinct action when it differs from the "all" action above
        // (i.e. there are other undefined steps in the file besides this one).
        if (stepAtCursor != allUndefined[0])
        {
            var stepText = GetStepText(stepAtCursor);
            actions.InsertRange(0, _actionBuilder.Build(target, $"Define step: {stepText}", new[] { stepAtCursor }));
        }

        if (actions.Count > MaxTargetedActions)
            actions = actions.Take(MaxTargetedActions).ToList();

        _logger.LogVerbose($"CodeActionHandler: {actions.Count} action(s) for {uri}");

        // Telemetry: records that a "Define step(s)" action was *offered*, not that the user
        // accepted it — the CodeAction's WorkspaceEdit is applied entirely client-side
        // (workspace/applyEdit), so unlike CommentToggleHandler's workspace/executeCommand round
        // trip, the server has no signal for whether the lightbulb was actually clicked. Undefined
        // step count is the closest available proxy for "how much work this would have saved."
        if (actions.Count > 0)
        {
            _telemetryService?.SendEvent("DefineSteps command offered", new()
            {
                ["UndefinedStepCount"] = allUndefined.Count,
                ["ActionsOffered"] = actions.Count,
            });
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(
            new CommandOrCodeActionContainer(actions));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the step (if any) that the request's cursor position falls on.</summary>
    private LSP.Core.Matching.StepBindingMatch? ResolveStepAtCursor(
        DocumentUri uri,
        Position position,
        LSP.Core.Matching.FeatureBindingMatchSet? matchSet)
    {
        if (matchSet is null) return null;
        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null) return null;

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(position.Line, position.Character);
        return matchSet.FindAt(offset);
    }

    private static string GetStepText(LSP.Core.Matching.StepBindingMatch step)
    {
        var item = step.Result.Items.FirstOrDefault(
            i => i.Type == LSP.Core.Matching.MatchResultType.Undefined);
        return item?.UndefinedStep?.StepText ?? string.Empty;
    }
}
