using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>textDocument/codeAction</c> requests for <c>*.feature</c> files: generates C#
/// step-definition stubs for undefined steps (Define Steps), offers "Insert '&lt;keyword&gt;'"
/// fixes for Gherkin syntax errors, and offers "Go to '&lt;method&gt;'" navigation for ambiguous
/// steps — the last of these only for VS Code (see <see cref="ClientIdeContext.IsVSCode"/>).
/// Registered via OmniSharp dynamic registration (<see cref="ICodeActionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
/// <remarks>
/// Reduced to guards, orchestration, and telemetry (issue #588, extended for #563): resolving
/// <em>where</em> generated code should go is <see cref="StepDefinitionTargetResolver"/>, and
/// building the actions for a given target/step-subset is <see cref="DefineStepsActionBuilder"/>.
/// The parser-error and ambiguous-step fixes added for issue #563 follow the same split:
/// <see cref="ParserErrorActionBuilder"/> and <see cref="AmbiguousStepActionBuilder"/>. All four
/// are plain internal collaborators constructed here, not DI-registered services — they have no
/// other consumer and no reason to be swapped independently of this handler.
/// </remarks>
public sealed class CodeActionHandler : ICodeActionHandler
{
    /// <summary>
    /// Caps the total number of code actions returned for one request (append candidates + the
    /// new-file fallback, across both the per-step and "all"/"scenario" title groups, plus any
    /// parser-error/ambiguous-step fixes).
    /// </summary>
    private const int MaxTargetedActions = 6;

    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IDocumentBufferService        _bufferService;
    private readonly IIdeSupportLogger               _logger;
    private readonly ClientIdeContext              _clientIde;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;
    private readonly StepDefinitionTargetResolver  _targetResolver;
    private readonly DefineStepsActionBuilder      _actionBuilder;
    private readonly ParserErrorActionBuilder      _parserErrorActionBuilder;
    private readonly AmbiguousStepActionBuilder    _ambiguousActionBuilder;

    /// <summary>Initializes a new instance of the <see cref="CodeActionHandler"/> class.</summary>
    public CodeActionHandler(
        IBindingMatchService      matchService,
        IStepScaffoldService      scaffoldService,
        ILspWorkspaceScopeManager scopeManager,
        IDocumentBufferService    bufferService,
        IIdeSupportLogger            logger,
        IFileSystemForIDE         fileSystem,
        ICompletionService        completionService,
        IErrorTelemetryService    errorTelemetryService,
        ClientIdeContext          clientIde,
        ILspTelemetryService?     telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _bufferService   = bufferService;
        _logger          = logger;
        _clientIde       = clientIde;
        _telemetryService = telemetryService;
        _recorder        = recorder ?? NullOperationDurationRecorder.Instance;
        _targetResolver  = new StepDefinitionTargetResolver(scopeManager, fileSystem);
        _actionBuilder   = new DefineStepsActionBuilder(scaffoldService, fileSystem);
        _parserErrorActionBuilder = new ParserErrorActionBuilder(completionService, errorTelemetryService);
        _ambiguousActionBuilder   = new AmbiguousStepActionBuilder(fileSystem);
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

        // Honour context.only (issue #563): every action this handler produces is a QuickFix, so
        // a request scoped to some other kind (e.g. Refactor) gets nothing from us.
        if (!QuickFixRequested(request.Context?.Only))
        {
            _logger.LogVerbose($"CodeActionHandler: requested kind(s) exclude QuickFix for {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer());
        }

        _bufferService.TryGet(uri, out var buffer);

        // Resolve the match set for the feature file's primary owner.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var matchKey = primaryOwner is not null
            ? new MatchSetKey(uri.ToString(),
                new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker))
            : MatchSetKey.ForUnknownProject(uri.ToString());

        _matchService.TryGet(matchKey, out var matchSet);

        var offset       = ResolveCursorOffset(buffer, request.Range.Start);
        var stepAtCursor = offset is int o ? matchSet.FindAt(o) : null;

        var actions = new List<CommandOrCodeAction>();
        var isDefineAction = new HashSet<CommandOrCodeAction>();

        // ── "Define missing step" actions ───────────────────────────────────────
        // Only offered when the request's cursor position actually falls on an undefined step
        // that has step text to build a skeleton from. Without the first check, a lightbulb
        // invoked over an ambiguous (or otherwise bound) step would still offer to "define" some
        // unrelated undefined step elsewhere in the file, which is misleading — that step has
        // nothing to do with what's under the cursor. Without the second, a bare keyword with no
        // step text (e.g. a lone "Given") would offer to generate a meaningless empty-expression
        // binding, since there is no text to build one from (issue #622).
        if (stepAtCursor is { IsUndefined: true } && !string.IsNullOrWhiteSpace(GetStepText(stepAtCursor)))
        {
            var defineActions = BuildDefineStepActions(uri, primaryOwner, matchSet, stepAtCursor);
            isDefineAction.UnionWith(defineActions);
            actions.AddRange(defineActions);
        }

        // ── "Go to '<method>'" actions for an ambiguous step under the cursor ───
        // VS Code-only (issue #563 follow-up): these actions carry only a `vscode.open` Command,
        // no Edit. VS Code's LSP client recognizes that command name and runs it locally; Visual
        // Studio and Rider have no such special-casing, forward it to the server via
        // workspace/executeCommand instead, and get back "Method not found" (confirmed live in
        // VS) — so the action would silently do nothing there. See ClientIdeContext.IsVSCode.
        if (stepAtCursor is { IsAmbiguous: true } && _clientIde.IsVSCode)
        {
            actions.AddRange(_ambiguousActionBuilder.Build(stepAtCursor));
        }

        // ── "Insert '<keyword>'" actions for a parser error under the cursor ────
        if (buffer?.Tags is not null)
        {
            var errorTag = FindParserErrorTagAt(buffer.Tags, offset);
            if (errorTag is not null)
            {
                var gherkinDoc = buffer.Tags
                    .FirstOrDefault(t => t.Type == IdeSupportTagTypes.Document)?.Data as IdeSupportGherkinDocument;
                actions.AddRange(_parserErrorActionBuilder.Build(uri, errorTag, gherkinDoc));
            }
        }

        // Honour context.diagnostics (issue #563): when the client scopes the request to specific
        // diagnostics (e.g. "fix this squiggle"), only return actions attributed to one of them.
        actions = FilterByContextDiagnostics(actions, request.Context?.Diagnostics);

        if (actions.Count > MaxTargetedActions)
            actions = actions.Take(MaxTargetedActions).ToList();

        _logger.LogVerbose($"CodeActionHandler: {actions.Count} action(s) for {uri}");

        // Telemetry: records that a "Define step(s)" action was *offered*, not that the user
        // accepted it — the CodeAction's WorkspaceEdit is applied entirely client-side
        // (workspace/applyEdit), so unlike CommentToggleHandler's workspace/executeCommand round
        // trip, the server has no signal for whether the lightbulb was actually clicked. Counted
        // from the final, post-filter/post-cap list — not the raw count built above — so this
        // never reports more than what the client actually received.
        var defineActionsOffered = actions.Count(isDefineAction.Contains);
        if (defineActionsOffered > 0)
        {
            _telemetryService?.SendEvent(TelemetryEvents.DefineStepsCommandOffered, new()
            {
                ["UndefinedStepCount"] = matchSet.Undefined.Count(),
                ["ActionsOffered"] = defineActionsOffered,
            });
        }

        return Task.FromResult<CommandOrCodeActionContainer?>(
            new CommandOrCodeActionContainer(actions));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<CommandOrCodeAction> BuildDefineStepActions(
        DocumentUri uri,
        LspReqnrollProject? primaryOwner,
        FeatureBindingMatchSet matchSet,
        StepBindingMatch stepAtCursor)
    {
        var allUndefined = matchSet.Undefined.ToList();
        var featurePath  = uri.GetFileSystemPath();
        var target       = _targetResolver.Resolve(uri, featurePath, primaryOwner, matchSet);

        // Per-step ("cursor") actions are inserted first so they survive the MaxTargetedActions
        // cap in Handle when both this and the "all"/"scenario" group are present.
        var actions = new List<CommandOrCodeAction>();

        // ── "Define all missing steps in file" ─────────────────────────────────
        actions.AddRange(_actionBuilder.Build(target,
            allUndefined.Count == 1 ? "Define missing step" : "Define all missing steps in file",
            allUndefined));

        // ── Per-step action for the step actually under the cursor ─────────────
        // Only add it as a distinct action when it differs from the "all" action above
        // (i.e. there are other undefined steps in the file besides this one).
        if (stepAtCursor != allUndefined[0])
        {
            var stepText = GetStepText(stepAtCursor);
            actions.InsertRange(0, _actionBuilder.Build(target, $"Define step: {stepText}", new[] { stepAtCursor }));
        }

        return actions;
    }

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    private static bool QuickFixRequested(Container<CodeActionKind>? only)
    {
        var kinds = only?.ToList();
        return kinds is null || kinds.Count == 0 || kinds.Contains(CodeActionKind.QuickFix);
    }

    /// <summary>Resolves the request position to an absolute character offset in the document, or <see langword="null"/> when no buffer is available.</summary>
    private static int? ResolveCursorOffset(DocumentBuffer? buffer, Position position)
    {
        if (buffer is null) return null;
        var snapshot = buffer.ToGherkinTextSnapshot();
        return snapshot.ToOffset(position.Line, position.Character);
    }

    /// <summary>Finds the parser-error tag (if any) whose span contains <paramref name="offset"/>.</summary>
    private static IdeSupportTag? FindParserErrorTagAt(IReadOnlyCollection<IdeSupportTag> tags, int? offset)
    {
        if (offset is not int o) return null;
        return tags.FirstOrDefault(t =>
            t.Type == IdeSupportTagTypes.ParserError && o >= t.Range.Start && o <= t.Range.End);
    }

    /// <summary>
    /// Restricts <paramref name="actions"/> to those attributed to one of
    /// <paramref name="contextDiagnostics"/> when the client named specific diagnostics; returns
    /// <paramref name="actions"/> unchanged when the context carries none (the common case — most
    /// <c>codeAction</c> requests are unscoped cursor-position polling, not "fix this squiggle").
    /// </summary>
    private static List<CommandOrCodeAction> FilterByContextDiagnostics(
        List<CommandOrCodeAction> actions,
        Container<Diagnostic>?    contextDiagnostics)
    {
        var contextList = contextDiagnostics?.ToList();
        if (contextList is null || contextList.Count == 0)
            return actions;

        return actions.Where(a =>
            a.CodeAction?.Diagnostics is not { } diagnostics ||
            diagnostics.Any(d => contextList.Any(cd => Overlaps(cd, d))))
            .ToList();
    }

    private static bool Overlaps(Diagnostic a, Diagnostic b) =>
        a.Source == b.Source
        && ComparePosition(a.Range.Start, b.Range.End) <= 0
        && ComparePosition(b.Range.Start, a.Range.End) <= 0;

    private static int ComparePosition(Position x, Position y)
    {
        var byLine = x.Line.CompareTo(y.Line);
        return byLine != 0 ? byLine : x.Character.CompareTo(y.Character);
    }

    private static string GetStepText(StepBindingMatch step)
    {
        var item = step.Result.Items.FirstOrDefault(
            i => i.Type == MatchResultType.Undefined);
        return item?.UndefinedStep?.StepText ?? string.Empty;
    }
}
