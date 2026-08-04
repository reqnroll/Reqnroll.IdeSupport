using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefs;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Thin convenience wrappers that send raw LSP messages by method name, so the specs do not
/// depend on which strongly-typed extension methods a given OmniSharp version exposes.
/// </summary>
public static class LspClientExtensions
{
    public static void OpenDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Version = version,
                LanguageId = "Gherkin",
                Text = text
            }
        });

    public static void ChangeDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didChange", new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = text })
        });

    /// <summary>Opens a C# document (languageId <c>csharp</c>), used to drive Roslyn binding discovery.</summary>
    public static void OpenCSharpDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Version = version,
                LanguageId = "csharp",
                Text = text
            }
        });

    /// <summary>Changes a C# document with the full updated text (full-sync), driving re-discovery.</summary>
    public static void ChangeCSharpDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.ChangeDocument(uri, version, text);

    public static void CloseDocument(this ILanguageClient client, DocumentUri uri)
        => client.SendNotification("textDocument/didClose", new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri }
        });

    public static Task<SemanticTokens?> RequestSemanticTokensAsync(
        this ILanguageClient client, DocumentUri uri, CancellationToken ct = default)
        => client.SendRequest("textDocument/semanticTokens/full",
                new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .Returning<SemanticTokens?>(ct);

    public static Task<SemanticTokens?> RequestSemanticTokensRangeAsync(
        this ILanguageClient client, DocumentUri uri, LspRange range, CancellationToken ct = default)
        => client.SendRequest("textDocument/semanticTokens/range",
                new SemanticTokensRangeParams { TextDocument = new TextDocumentIdentifier { Uri = uri }, Range = range })
            .Returning<SemanticTokens?>(ct);

    public static void SendProjectLoaded(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectLoaded", payload);

    public static void SendProjectUnloaded(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectUnloaded", payload);

    public static void SendProjectFiles(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectFiles", payload);

    public static Task<LocationOrLocationLinks?> RequestReferencesAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("textDocument/references",
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                    Context      = new ReferenceContext { IncludeDeclaration = false }
                })
            .Returning<LocationOrLocationLinks?>(ct);

    /// <summary>
    /// Sends a <c>reqnroll/findStepUsages</c> request.
    /// Returns <see langword="null"/> when the caret is not on a binding (three-state contract).
    /// </summary>
    public static Task<FindStepUsagesResponse?> RequestFindStepUsagesAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("reqnroll/findStepUsages",
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                    Context      = new ReferenceContext { IncludeDeclaration = false }
                })
            .Returning<FindStepUsagesResponse?>(ct);

    /// <summary>Sends a <c>reqnroll/goToHooks</c> request (F17 — Hook Navigation).</summary>
    public static Task<GoToHooksResponse?> RequestGoToHooksAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("reqnroll/goToHooks",
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                })
            .Returning<GoToHooksResponse?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/codeLens</c> request (F18 — Step Code Lens).
    /// Returns a CodeLens array for .cs files with step-binding attributes,
    /// or <see langword="null"/> for non-.cs files.
    /// </summary>
    public static Task<CodeLens[]?> RequestCodeLensAsync(
        this ILanguageClient client, DocumentUri uri, CancellationToken ct = default)
        => client.SendRequest("textDocument/codeLens",
                new CodeLensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .Returning<CodeLens[]?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/formatting</c> request (F11 — Document Auto-formatting).
    /// </summary>
    public static Task<TextEdit[]?> RequestFormattingAsync(
        this ILanguageClient client, DocumentUri uri,
        int tabSize = 4, bool insertSpaces = true, CancellationToken ct = default)
        => client.SendRequest("textDocument/formatting",
                new DocumentFormattingParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Options = new FormattingOptions { TabSize = tabSize, InsertSpaces = insertSpaces }
                })
            .Returning<TextEdit[]?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/rangeFormatting</c> request (F11 — Document Auto-formatting).
    /// </summary>
    public static Task<TextEdit[]?> RequestRangeFormattingAsync(
        this ILanguageClient client, DocumentUri uri,
        int startLine, int endLine,
        int tabSize = 4, bool insertSpaces = true, CancellationToken ct = default)
        => client.SendRequest("textDocument/rangeFormatting",
                new DocumentRangeFormattingParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(startLine, 0), new Position(endLine, 0)),
                    Options = new FormattingOptions { TabSize = tabSize, InsertSpaces = insertSpaces }
                })
            .Returning<TextEdit[]?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/onTypeFormatting</c> request (F12 — Table Auto-formatting).
    /// </summary>
    public static Task<TextEdit[]?> RequestOnTypeFormattingAsync(
        this ILanguageClient client, DocumentUri uri,
        int line, int character, string triggerCharacter,
        int tabSize = 4, bool insertSpaces = true, CancellationToken ct = default)
        => client.SendRequest("textDocument/onTypeFormatting",
                new DocumentOnTypeFormattingParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                    Character    = triggerCharacter,
                    Options      = new FormattingOptions { TabSize = tabSize, InsertSpaces = insertSpaces }
                })
            .Returning<TextEdit[]?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/documentSymbol</c> request (F9 — Document Outline).
    /// Returns the nested symbol hierarchy for the given feature file.
    /// </summary>
    public static Task<SymbolInformationOrDocumentSymbolContainer?> RequestDocumentSymbolAsync(
        this ILanguageClient client, DocumentUri uri, CancellationToken ct = default)
        => client.SendRequest("textDocument/documentSymbol",
                new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .Returning<SymbolInformationOrDocumentSymbolContainer?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/completion</c> request (F7 keyword completion, F8 step completion).
    /// </summary>
    public static Task<CompletionList?> RequestCompletionAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character,
        CancellationToken ct = default)
        => client.SendRequest("textDocument/completion",
                new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character)
                })
            .Returning<CompletionList?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/foldingRange</c> request (F10 — Code Folding).
    /// Returns the foldable region ranges for the given feature file.
    /// </summary>
    public static Task<Container<FoldingRange>?> RequestFoldingRangeAsync(
        this ILanguageClient client, DocumentUri uri, CancellationToken ct = default)
        => client.SendRequest("textDocument/foldingRange",
                new FoldingRangeRequestParam { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .Returning<Container<FoldingRange>?>(ct);

    /// <summary>
    /// Sends a <c>workspace/executeCommand</c> request (F13 — Comment/Uncomment).
    /// </summary>
    public static Task RequestCommandAsync(
        this ILanguageClient client, ExecuteCommandParams commandParams, CancellationToken ct = default)
        => client.SendRequest("workspace/executeCommand", commandParams)
            .Returning<Unit>(ct);

    /// <summary>
    /// Sends a <c>textDocument/codeAction</c> request (F6 — Define Steps).
    /// Returns the code actions available at <paramref name="range"/> in the given document,
    /// or <see langword="null"/> when no actions are offered.
    /// </summary>
    public static Task<CommandOrCodeActionContainer?> RequestCodeActionsAsync(
        this ILanguageClient client, DocumentUri uri, LspRange range, CancellationToken ct = default)
        => client.SendRequest("textDocument/codeAction",
                new CodeActionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Range        = range,
                    Context      = new CodeActionContext()
                })
            .Returning<CommandOrCodeActionContainer?>(ct);

    /// <summary>
    /// Sends a <c>reqnroll/goToStepDefinitions</c> request (F5 — Go to Step Definition).
    /// Returns step definition locations with type and method metadata for a cursor in a .feature file.
    /// </summary>
    public static Task<GoToStepDefinitionsResponse?> RequestGoToStepDefinitionsAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("reqnroll/goToStepDefinitions",
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character)
                })
            .Returning<GoToStepDefinitionsResponse?>(ct);

    /// <summary>
    /// Sends a <c>workspace/didChangeWatchedFiles</c> notification with a single Deleted event
    /// for the given <c>.cs</c> URI, simulating what VS Code's LSP client sends when a source
    /// file is removed from disk.
    /// </summary>
    public static void NotifyCsFileDeleted(this ILanguageClient client, DocumentUri uri)
        => client.SendNotification("workspace/didChangeWatchedFiles", new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(
                new FileEvent { Uri = uri, Type = FileChangeType.Deleted })
        });

    /// <summary>
    /// Sends a <c>reqnroll/findUnusedStepDefinitions</c> request (F15 — Find Unused).
    /// Returns binding expressions with zero matching feature steps across the workspace.
    /// </summary>
    public static Task<FindUnusedStepDefinitionsResponse?> RequestFindUnusedStepDefinitionsAsync(
        this ILanguageClient client, CancellationToken ct = default)
        => client.SendRequest("reqnroll/findUnusedStepDefinitions",
                new FindUnusedStepDefinitionsParams())
            .Returning<FindUnusedStepDefinitionsResponse?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/prepareRename</c> request (F16 — Step Rename).
    /// Returns the renameable range if the cursor is on a step binding, else null. A
    /// <c>.feature</c>-triggered result carries a <see cref="PlaceholderRange"/> (the abstract
    /// expression, not the concrete step text) rather than a bare <see cref="LspRange"/> — see
    /// issue #33 follow-up.
    /// </summary>
    public static Task<RangeOrPlaceholderRange?> RequestPrepareRenameAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("textDocument/prepareRename",
                new PrepareRenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character)
                })
            .Returning<RangeOrPlaceholderRange?>(ct);

    /// <summary>
    /// Sends a <c>textDocument/rename</c> request (F16 — Step Rename).
    /// Returns a WorkspaceEdit covering all .feature and .cs edits required to rename the step.
    /// </summary>
    public static Task<WorkspaceEdit?> RequestRenameAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, string newName,
        CancellationToken ct = default)
        => client.SendRequest("textDocument/rename",
                new RenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                    NewName      = newName
                })
            .Returning<WorkspaceEdit?>(ct);

    /// <summary>
    /// Sends a <c>reqnroll/renameTargets</c> request (F16 — multi-attribute picker).
    /// Returns all renameable binding attributes at the cursor for the user to choose from.
    /// </summary>
    public static Task<RenameTargetsResponse?> RequestRenameTargetsAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("reqnroll/renameTargets",
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character)
                })
            .Returning<RenameTargetsResponse?>(ct);

    /// <summary>
    /// Sends a <c>reqnroll/selectRenameTarget</c> notification (F16).
    /// Pre-selects which attribute to rename before the next <c>textDocument/rename</c>.
    /// </summary>
    public static void SendSelectRenameTarget(
        this ILanguageClient client, string uri, int version, int attributeIndex)
        => client.SendNotification("reqnroll/selectRenameTarget",
                new SelectRenameTargetParams { Uri = uri, Version = version, AttributeIndex = attributeIndex });
}
