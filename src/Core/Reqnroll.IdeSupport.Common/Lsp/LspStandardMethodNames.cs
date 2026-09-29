namespace Reqnroll.IdeSupport.Common.Lsp;

/// <summary>
/// Centralizes the standard LSP <c>textDocument/*</c>, <c>workspace/*</c>, <c>codeLens/*</c>
/// and <c>telemetry/*</c> method names shared by <c>Reqnroll.IdeSupport.LSP.Server</c>
/// and all IDE client extensions.
/// </summary>
/// <remarks>
/// Custom <c>reqnroll/*</c> method names are kept in each project's own class
/// (<c>LspMethodNames</c> in the server, <c>ReqnrollMethodNames</c> in the VS extension,
/// <c>ReqnrollMethods</c> in the VS Code extension, LSP4J interfaces in Rider) since
/// those are per-project by necessity and have no cross-project reuse benefit.
/// </remarks>
public static class LspStandardMethodNames
{
    /// <summary>Method name for the <c>textDocument/semanticTokens/full</c> request.</summary>
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    /// <summary>Method name for the <c>textDocument/semanticTokens/full/delta</c> request.</summary>
    public const string TextDocumentSemanticTokensFullDelta = "textDocument/semanticTokens/full/delta";
    /// <summary>Method name for the <c>textDocument/semanticTokens/range</c> request.</summary>
    public const string TextDocumentSemanticTokensRange = "textDocument/semanticTokens/range";
    /// <summary>Method name for the <c>textDocument/completion</c> request.</summary>
    public const string TextDocumentCompletion = "textDocument/completion";
    /// <summary>Method name for the <c>textDocument/definition</c> request.</summary>
    public const string TextDocumentDefinition = "textDocument/definition";
    /// <summary>Method name for the <c>textDocument/references</c> request.</summary>
    public const string TextDocumentReferences = "textDocument/references";
    /// <summary>Method name for the <c>textDocument/codeLens</c> request.</summary>
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    /// <summary>Method name for the <c>codeLens/resolve</c> request.</summary>
    public const string CodeLensResolve = "codeLens/resolve";
    /// <summary>Method name for the <c>textDocument/inlayHint</c> request.</summary>
    public const string TextDocumentInlayHint = "textDocument/inlayHint";
    /// <summary>Method name for the <c>textDocument/foldingRange</c> request.</summary>
    public const string TextDocumentFoldingRange = "textDocument/foldingRange";
    /// <summary>Method name for the <c>textDocument/prepareRename</c> request.</summary>
    public const string TextDocumentPrepareRename = "textDocument/prepareRename";
    /// <summary>Method name for the <c>textDocument/rename</c> request.</summary>
    public const string TextDocumentRename = "textDocument/rename";
    /// <summary>Method name for the <c>textDocument/publishDiagnostics</c> notification.</summary>
    public const string TextDocumentPublishDiagnostics = "textDocument/publishDiagnostics";
    /// <summary>Method name for the <c>textDocument/formatting</c> request.</summary>
    public const string TextDocumentFormatting = "textDocument/formatting";
    /// <summary>Method name for the <c>textDocument/rangeFormatting</c> request.</summary>
    public const string TextDocumentRangeFormatting = "textDocument/rangeFormatting";
    /// <summary>Method name for the <c>textDocument/onTypeFormatting</c> request.</summary>
    public const string TextDocumentOnTypeFormatting = "textDocument/onTypeFormatting";
    /// <summary>Method name for the <c>textDocument/codeAction</c> request.</summary>
    public const string TextDocumentCodeAction = "textDocument/codeAction";
    /// <summary>Method name for the <c>textDocument/documentSymbol</c> request.</summary>
    public const string TextDocumentDocumentSymbol = "textDocument/documentSymbol";
    /// <summary>Method name for the <c>textDocument/didOpen</c> notification.</summary>
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    /// <summary>Method name for the <c>textDocument/didChange</c> notification.</summary>
    public const string TextDocumentDidChange = "textDocument/didChange";
    /// <summary>Method name for the <c>textDocument/didClose</c> notification.</summary>
    public const string TextDocumentDidClose = "textDocument/didClose";

    // ── Workspace Methods ───────────────────────────────────────────────────
    /// <summary>Method name for the <c>workspace/applyEdit</c> request.</summary>
    public const string WorkspaceApplyEdit = "workspace/applyEdit";
    /// <summary>Method name for the <c>workspace/codeLens/refresh</c> request.</summary>
    public const string WorkspaceCodeLensRefresh = "workspace/codeLens/refresh";
    /// <summary>Method name for the <c>workspace/didChangeWatchedFiles</c> notification.</summary>
    public const string WorkspaceDidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
    /// <summary>Method name for the <c>workspace/executeCommand</c> request.</summary>
    public const string WorkspaceExecuteCommand = "workspace/executeCommand";
    /// <summary>Method name for the <c>workspace/didChangeWorkspaceFolders</c> notification.</summary>
    public const string WorkspaceDidChangeWorkspaceFolders = "workspace/didChangeWorkspaceFolders";
    /// <summary>Method name for the <c>workspace/semanticTokens/refresh</c> request.</summary>
    public const string WorkspaceSemanticTokensRefresh = "workspace/semanticTokens/refresh";
    /// <summary>Method name for the <c>workspace/inlayHint/refresh</c> request.</summary>
    public const string WorkspaceInlayHintRefresh = "workspace/inlayHint/refresh";

    // ── Telemetry ───────────────────────────────────────────────────────────
    /// <summary>Method name for the <c>telemetry/event</c> notification.</summary>
    public const string TelemetryEvent = "telemetry/event";
}