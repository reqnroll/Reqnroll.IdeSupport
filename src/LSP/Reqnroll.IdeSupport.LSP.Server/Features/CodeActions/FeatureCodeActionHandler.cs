using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>textDocument/codeAction</c> requests for <c>*.feature</c> files (Define Steps).
/// Returns code actions that generate C# step-definition stubs for undefined steps.
/// Registered via OmniSharp dynamic registration (<see cref="ICodeActionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
public sealed class FeatureCodeActionHandler : ICodeActionHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly IStepScaffoldService          _scaffoldService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IDocumentBufferService        _bufferService;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="FeatureCodeActionHandler"/> class.</summary>
    public FeatureCodeActionHandler(
        IBindingMatchService      matchService,
        IStepScaffoldService      scaffoldService,
        ILspWorkspaceScopeManager scopeManager,
        IDocumentBufferService    bufferService,
        IIdeSupportLogger            logger,
        ILspTelemetryService?     telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService    = matchService;
        _scaffoldService = scaffoldService;
        _scopeManager    = scopeManager;
        _bufferService   = bufferService;
        _logger          = logger;
        _telemetryService = telemetryService;
        _recorder        = recorder ?? NullOperationDurationRecorder.Instance;
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
            _logger.LogVerbose($"FeatureCodeActionHandler: ignoring non-.feature URI {uri}");
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
            _logger.LogVerbose($"FeatureCodeActionHandler: no undefined steps for {uri}");
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
            _logger.LogVerbose($"FeatureCodeActionHandler: no undefined step at the request position in {uri}");
            return Task.FromResult<CommandOrCodeActionContainer?>(new CommandOrCodeActionContainer());
        }

        // Read skeleton style from project config.
        var configProvider = _scopeManager.GetConfigurationProviderForUri(uri);
        var config = configProvider.GetConfiguration();
        var style  = config?.SnippetExpressionStyle ?? SnippetExpressionStyle.CucumberExpression;
        var csharpConfig = new CSharpCodeGenerationConfiguration();

        // Determine target file metadata.
        var featurePath   = uri.GetFileSystemPath();
        var className     = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);
        var defaultNs     = primaryOwner?.DefaultNamespace ?? Path.GetFileNameWithoutExtension(featurePath);
        var projectFolder = primaryOwner?.ProjectFolder ?? Path.GetDirectoryName(featurePath) ?? string.Empty;
        var bindingPaths  = primaryOwner is not null
            ? _scopeManager.GetBindingFilePathsForProject(primaryOwner)
            : (IReadOnlyCollection<string>)Array.Empty<string>();
        var targetFolder  = FindBestTargetFolder(bindingPaths, featurePath);
        var targetPath = Path.Combine(targetFolder, className + ".cs");
        if (File.Exists(targetPath))
        {
            int suffix = 2;
            while (File.Exists(Path.Combine(targetFolder, className + suffix + ".cs")))
                suffix++;
            targetPath = Path.Combine(targetFolder, className + suffix + ".cs");
        }
        className = Path.GetFileNameWithoutExtension(targetPath);
        var @namespace    = StepDefinitionFileBuilder.DeriveNamespace(projectFolder, defaultNs, targetPath);

        const string indent  = "    ";
        var          newLine = Environment.NewLine;

        // Binds the target-file/style parameters shared by every "Define step(s)" action for
        // this request, so the two call sites below only ever vary by title/steps — a future
        // parameter added here (e.g. a different style per step) can't be updated in one call
        // site and forgotten in the other.
        CodeAction? BuildDefineStepsAction(string title, IEnumerable<LSP.Core.Matching.StepBindingMatch> steps) =>
            BuildAction(title, steps, style, csharpConfig, className, @namespace, targetPath, indent, newLine);

        // Collect actions.
        var actions = new List<CommandOrCodeAction>();

        // ── "Define all missing steps in file" ─────────────────────────────────
        if (allUndefined.Count >= 1)
        {
            var action = BuildDefineStepsAction(
                allUndefined.Count == 1 ? "Define missing step" : "Define all missing steps in file",
                allUndefined);

            if (action is not null) actions.Add(new CommandOrCodeAction(action));
        }

        // ── Per-step action for the step actually under the cursor ─────────────
        // Only add it as a distinct action when it differs from the "all" action above
        // (i.e. there are other undefined steps in the file besides this one).
        if (stepAtCursor != allUndefined[0])
        {
            var stepText = GetStepText(stepAtCursor);
            var singleAction = BuildDefineStepsAction($"Define step: {stepText}", new[] { stepAtCursor });

            if (singleAction is not null)
                actions.Insert(0, new CommandOrCodeAction(singleAction));
        }

        _logger.LogVerbose($"FeatureCodeActionHandler: {actions.Count} action(s) for {uri}");

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

    private CodeAction? BuildAction(
        string                            title,
        IEnumerable<LSP.Core.Matching.StepBindingMatch> steps,
        SnippetExpressionStyle            style,
        CSharpCodeGenerationConfiguration csharpConfig,
        string                            className,
        string                            @namespace,
        string                            targetPath,
        string                            indent,
        string                            newLine)
    {
        var descriptors = _scaffoldService.BuildDescriptors(steps, style);
        if (descriptors.Count == 0) return null;

        var snippets = descriptors
            .Select(d => StepSkeletonRenderer.Render(d, indent, newLine))
            .ToList();

        var fileContent = StepDefinitionFileBuilder.BuildNewFile(
            snippets, className, @namespace, csharpConfig, indent, newLine);

        var targetUri = DocumentUri.FromFileSystemPath(targetPath);

        var edit = new WorkspaceEdit
        {
            DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                new WorkspaceEditDocumentChange(new CreateFile
                {
                    Uri     = targetUri,
                    Options = new CreateFileOptions { IgnoreIfExists = true }
                }),
                new WorkspaceEditDocumentChange(new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri     = targetUri,
                        Version = null
                    },
                    Edits = new TextEditContainer(new TextEdit
                    {
                        Range   = new LspRange(new Position(0, 0), new Position(0, 0)),
                        NewText = fileContent
                    })
                }))
        };

        return new CodeAction
        {
            Title       = title,
            Kind        = CodeActionKind.QuickFix,
            Edit        = edit,
            // VS Code executes this command after applying the edit, opening the new file.
            // Other clients receive an unknown command they can safely ignore.
            Command     = new Command
            {
                Title     = "Open step definition file",
                Name      = "vscode.open",
                Arguments = new JArray(targetUri.ToString())
            },
            IsPreferred = true
        };
    }

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

    /// <summary>
    /// Picks the best target directory for a new step-definition file.
    /// Prefers the folder that already holds the most binding files (so the generated file
    /// lands alongside the user's existing step definitions), then falls back to a sibling
    /// StepDefinitions/ folder or the feature file's own directory.
    /// </summary>
    private static string FindBestTargetFolder(
        IReadOnlyCollection<string> bindingFiles,
        string featureFilePath)
    {
        if (bindingFiles.Count > 0)
        {
            var best = bindingFiles
                .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
                .Where(d => d.Length > 0)
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (best is not null)
                return best.Key;
        }

        var featureDir    = Path.GetDirectoryName(featureFilePath) ?? string.Empty;
        var siblingStepDefs = Path.Combine(featureDir, "StepDefinitions");
        return Directory.Exists(siblingStepDefs) ? siblingStepDefs : featureDir;
    }
}
