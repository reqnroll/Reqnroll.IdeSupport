using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
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
public sealed class CodeActionHandler : ICodeActionHandler
{
    /// <summary>
    /// Caps how many existing binding files are offered as append targets for one "Define
    /// step(s)" title, so the lightbulb menu doesn't grow unbounded on a project with many
    /// binding files matched to a feature.
    /// </summary>
    private const int MaxAppendCandidates = 5;

    /// <summary>
    /// Caps the total number of code actions returned for one request (append candidates + the
    /// new-file fallback, across both the per-step and "all"/"scenario" title groups).
    /// </summary>
    private const int MaxTargetedActions = 6;

    private readonly IBindingMatchService          _matchService;
    private readonly IStepScaffoldService          _scaffoldService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IDocumentBufferService        _bufferService;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;
    private readonly IFileSystemForIDE             _fileSystem;

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
        _scaffoldService = scaffoldService;
        _scopeManager    = scopeManager;
        _bufferService   = bufferService;
        _logger          = logger;
        _fileSystem      = fileSystem;
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

        // Rank existing binding files by how many of *this feature's* steps are already matched
        // there — a stronger placement signal than "which folder has the most binding files
        // anywhere in the project" (used only as the fallback below). Capped so the lightbulb
        // menu doesn't grow unbounded (see MaxTargetedActions).
        var appendCandidates = (matchSet is not null
                ? CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet)
                : Array.Empty<string>())
            .Where(f => _fileSystem.File.Exists(f))
            .Take(MaxAppendCandidates)
            .ToList();

        // The new-file fallback's folder: alongside the top-ranked candidate (even when that
        // specific file is later declined for append), or the project-wide folder-frequency
        // heuristic only when the feature has no ranked candidates at all (e.g. a brand-new
        // feature with no bindings anywhere yet).
        var newFileFolder = appendCandidates.Count > 0
            ? Path.GetDirectoryName(appendCandidates[0]) is { Length: > 0 } dir ? dir : projectFolder
            : FindBestTargetFolder(_fileSystem, bindingPaths, featurePath);

        var targetPath = Path.Combine(newFileFolder, className + ".cs");
        if (_fileSystem.File.Exists(targetPath))
        {
            int suffix = 2;
            while (_fileSystem.File.Exists(Path.Combine(newFileFolder, className + suffix + ".cs")))
                suffix++;
            targetPath = Path.Combine(newFileFolder, className + suffix + ".cs");
        }
        className = Path.GetFileNameWithoutExtension(targetPath);
        var @namespace    = StepDefinitionFileBuilder.DeriveNamespace(projectFolder, defaultNs, targetPath);

        const string indent  = "    ";
        var          newLine = Environment.NewLine;

        // Builds one CodeAction per plausible target (append to an existing candidate file, or
        // create the new file) for a given title/step subset. Titles only gain a "→ <target>"
        // suffix when more than one target actually resolved — the common case (no existing
        // candidate) keeps the original unadorned title so most users never see a change.
        List<CommandOrCodeAction> BuildTargetedActions(string baseTitle, IReadOnlyList<LSP.Core.Matching.StepBindingMatch> steps)
        {
            var snippets = RenderSnippets(steps, style, indent, newLine);
            if (snippets is null) return new List<CommandOrCodeAction>();

            // Resolve which append candidates actually succeed *before* deciding titles, so a
            // candidate declined by AppendToFile (ambiguous brace structure) doesn't still cause
            // the surviving actions to get a "→ <target>" suffix implying a choice that isn't real.
            var successfulAppends = new List<(string TargetPath, string ExistingContent, string AppendedContent)>();
            foreach (var candidate in appendCandidates)
            {
                var existingContent = _fileSystem.File.ReadAllText(candidate);
                var appendedContent = StepDefinitionFileBuilder.AppendToFile(existingContent, snippets, indent, newLine);
                if (appendedContent is not null)
                    successfulAppends.Add((candidate, existingContent, appendedContent));
            }

            var newFileContent = StepDefinitionFileBuilder.BuildNewFile(
                snippets, className, @namespace, csharpConfig, indent, newLine);

            bool multiTarget = successfulAppends.Count > 0; // +1 for the always-present new-file target

            var actionsForTitle = new List<CodeAction>(successfulAppends.Count + 1);
            foreach (var (path, existingContent, appendedContent) in successfulAppends)
            {
                var title = multiTarget ? $"{baseTitle} → {Path.GetFileName(path)}" : baseTitle;
                actionsForTitle.Add(BuildAppendCodeAction(title, path, existingContent, appendedContent));
            }

            var newFileTitle = multiTarget ? $"{baseTitle} → new file" : baseTitle;
            actionsForTitle.Add(BuildCreateCodeAction(newFileTitle, newFileContent, targetPath));

            return actionsForTitle.Select(a => new CommandOrCodeAction(a)).ToList();
        }

        // Collect actions. Per-step ("cursor") actions are inserted first so they survive the
        // MaxTargetedActions cap below when both this and the "all"/"scenario" group are present.
        var actions = new List<CommandOrCodeAction>();

        // ── "Define all missing steps in file" ─────────────────────────────────
        if (allUndefined.Count >= 1)
        {
            actions.AddRange(BuildTargetedActions(
                allUndefined.Count == 1 ? "Define missing step" : "Define all missing steps in file",
                allUndefined));
        }

        // ── Per-step action for the step actually under the cursor ─────────────
        // Only add it as a distinct action when it differs from the "all" action above
        // (i.e. there are other undefined steps in the file besides this one).
        if (stepAtCursor != allUndefined[0])
        {
            var stepText = GetStepText(stepAtCursor);
            actions.InsertRange(0, BuildTargetedActions($"Define step: {stepText}", new[] { stepAtCursor }));
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

    /// <summary>Builds a "Define step(s)" action that creates a brand-new step-definition file with the given content.</summary>
    private static CodeAction BuildCreateCodeAction(string title, string fileContent, string targetPath)
    {
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

        return BuildCodeAction(title, edit, targetUri);
    }

    /// <summary>
    /// Builds a "Define step(s)" action that replaces an existing step-definition file's content
    /// with <paramref name="appendedContent"/> (the file plus the new method(s), already computed
    /// by <see cref="StepDefinitionFileBuilder.AppendToFile"/>). Every candidate offered here comes
    /// from <see cref="LSP.Core.Scaffolding.CandidateStepDefinitionFileRanker"/>, which only
    /// surfaces files that already contain a step definition matched to this feature — so, unlike
    /// a newly created file, no <c>[Binding]</c>-attribute check is needed before offering it.
    /// </summary>
    private static CodeAction BuildAppendCodeAction(string title, string targetPath, string existingContent, string appendedContent)
    {
        var targetUri = DocumentUri.FromFileSystemPath(targetPath);

        var edit = new WorkspaceEdit
        {
            DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                new WorkspaceEditDocumentChange(new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri     = targetUri,
                        Version = null
                    },
                    Edits = new TextEditContainer(new TextEdit
                    {
                        Range   = new LspRange(new Position(0, 0), EndPositionOf(existingContent)),
                        NewText = appendedContent
                    })
                }))
        };

        return BuildCodeAction(title, edit, targetUri);
    }

    private static CodeAction BuildCodeAction(string title, WorkspaceEdit edit, DocumentUri targetUri) =>
        new()
        {
            Title       = title,
            Kind        = CodeActionKind.QuickFix,
            Edit        = edit,
            // VS Code executes this command after applying the edit, opening the target file.
            // Other clients receive an unknown command they can safely ignore.
            Command     = new Command
            {
                Title     = "Open step definition file",
                Name      = "vscode.open",
                Arguments = new JArray(targetUri.ToString())
            },
            IsPreferred = true
        };

    private List<string>? RenderSnippets(
        IEnumerable<LSP.Core.Matching.StepBindingMatch> steps,
        SnippetExpressionStyle style,
        string indent,
        string newLine)
    {
        var descriptors = _scaffoldService.BuildDescriptors(steps, style);
        if (descriptors.Count == 0) return null;

        return descriptors
            .Select(d => StepSkeletonRenderer.Render(d, indent, newLine))
            .ToList();
    }

    /// <summary>The end position (last line, last character) of <paramref name="content"/>, for a full-document replace range.</summary>
    private static Position EndPositionOf(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var lastLineIndex = lines.Length - 1;
        return new Position(lastLineIndex, lines[lastLineIndex].Length);
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
        IFileSystemForIDE fileSystem,
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
        return fileSystem.Directory.Exists(siblingStepDefs) ? siblingStepDefs : featureDir;
    }
}
