using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Builds the "Define step(s)" <see cref="CodeAction"/>s for one title/step-subset group, given
/// an already-resolved <see cref="StepDefinitionTarget"/> (issue #588). Extracted from
/// <see cref="CodeActionHandler.Handle"/>'s nested <c>BuildTargetedActions</c> local function —
/// this class owns "given this target and these steps, build the actions", independent of where
/// the target came from or how many title groups the caller builds.
/// </summary>
internal sealed class DefineStepsActionBuilder
{
    private readonly IStepScaffoldService _scaffoldService;
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="DefineStepsActionBuilder"/> class.</summary>
    public DefineStepsActionBuilder(IStepScaffoldService scaffoldService, IFileSystemForIDE fileSystem)
    {
        _scaffoldService = scaffoldService;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Builds one <see cref="CodeAction"/> per plausible target (append to an existing candidate
    /// file, or create the new file) for <paramref name="baseTitle"/>/<paramref name="steps"/>.
    /// Titles only gain a "→ &lt;target&gt;" suffix when more than one target actually resolved —
    /// the common case (no existing candidate) keeps the original unadorned title so most users
    /// never see a change.
    /// </summary>
    public List<CommandOrCodeAction> Build(
        StepDefinitionTarget target,
        string baseTitle,
        IReadOnlyList<LSP.Core.Matching.StepBindingMatch> steps)
    {
        var snippets = RenderSnippets(steps, target.Style, target.Indent, target.NewLine);
        if (snippets is null) return new List<CommandOrCodeAction>();

        // Resolve which append candidates actually succeed *before* deciding titles, so a
        // candidate declined by AppendToFile (ambiguous brace structure) doesn't still cause
        // the surviving actions to get a "→ <target>" suffix implying a choice that isn't real.
        var successfulAppends = new List<(string TargetPath, string ExistingContent, string AppendedContent)>();
        foreach (var candidate in target.AppendCandidates)
        {
            var existingContent = _fileSystem.File.ReadAllText(candidate);
            var appendedContent = StepDefinitionFileBuilder.AppendToFile(
                existingContent, snippets, target.Indent, target.NewLine);
            if (appendedContent is not null)
                successfulAppends.Add((candidate, existingContent, appendedContent));
        }

        var newFileContent = StepDefinitionFileBuilder.BuildNewFile(
            snippets, target.ClassName, target.Namespace, target.CSharpConfig, target.Indent, target.NewLine);

        bool multiTarget = successfulAppends.Count > 0; // +1 for the always-present new-file target

        // Exactly one action per group is marked IsPreferred — the top-ranked append candidate
        // when one exists, otherwise the new-file fallback. Some clients (VS in particular)
        // don't preserve the server's array order in the lightbulb menu and instead lean on
        // this signal (or fall back to their own sort, e.g. alphabetical by title) to decide
        // what to show first, so relying on ordering alone isn't enough to keep "append to the
        // existing file" as the default choice.
        var actionsForTitle = new List<CodeAction>(successfulAppends.Count + 1);
        bool isFirst = true;
        foreach (var (path, existingContent, appendedContent) in successfulAppends)
        {
            var title = multiTarget ? $"{baseTitle} → {Path.GetFileName(path)}" : baseTitle;
            actionsForTitle.Add(BuildAppendCodeAction(title, path, existingContent, appendedContent, isPreferred: isFirst));
            isFirst = false;
        }

        var newFileTitle = multiTarget ? $"{baseTitle} → new file" : baseTitle;
        actionsForTitle.Add(BuildCreateCodeAction(newFileTitle, newFileContent, target.TargetPath, isPreferred: isFirst));

        return actionsForTitle.Select(a => new CommandOrCodeAction(a)).ToList();
    }

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

    /// <summary>Builds a "Define step(s)" action that creates a brand-new step-definition file with the given content.</summary>
    private static CodeAction BuildCreateCodeAction(string title, string fileContent, string targetPath, bool isPreferred)
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

        return BuildCodeAction(title, edit, targetUri, isPreferred);
    }

    /// <summary>
    /// Builds a "Define step(s)" action that replaces an existing step-definition file's content
    /// with <paramref name="appendedContent"/> (the file plus the new method(s), already computed
    /// by <see cref="StepDefinitionFileBuilder.AppendToFile"/>). Every candidate offered here comes
    /// from <see cref="LSP.Core.Scaffolding.CandidateStepDefinitionFileRanker"/>, which only
    /// surfaces files that already contain a step definition matched to this feature — so, unlike
    /// a newly created file, no <c>[Binding]</c>-attribute check is needed before offering it.
    /// </summary>
    private static CodeAction BuildAppendCodeAction(string title, string targetPath, string existingContent, string appendedContent, bool isPreferred)
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

        return BuildCodeAction(title, edit, targetUri, isPreferred);
    }

    /// <summary>
    /// Builds the shared <see cref="CodeAction"/> shape. <paramref name="isPreferred"/> should be
    /// <see langword="true"/> for exactly one action per title group — the client-facing signal
    /// for "this is the one to pick" (e.g. VS/VS Code bubble the preferred action to the top of
    /// the lightbulb menu instead of relying on array order, which some clients don't preserve).
    /// </summary>
    private static CodeAction BuildCodeAction(string title, WorkspaceEdit edit, DocumentUri targetUri, bool isPreferred) =>
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
            IsPreferred = isPreferred
        };

    /// <summary>The end position (last line, last character) of <paramref name="content"/>, for a full-document replace range.</summary>
    private static Position EndPositionOf(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var lastLineIndex = lines.Length - 1;
        return new Position(lastLineIndex, lines[lastLineIndex].Length);
    }
}
