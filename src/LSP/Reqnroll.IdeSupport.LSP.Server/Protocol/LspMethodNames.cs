namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// Centralizes all LSP method names (both standard and custom Reqnroll extensions)
/// used by the language server. This prevents magic strings scattered across the codebase
/// and makes refactoring or auditing registered endpoints much easier.
/// </summary>
public static class LspMethodNames
{
    // ── Custom Reqnroll Extensions ───────────────────────────────────────────
    /// <summary>Method name for the <c>reqnroll/projectLoaded</c> notification.</summary>
    public const string ReqnrollProjectLoaded = "reqnroll/projectLoaded";
    /// <summary>Method name for the <c>reqnroll/projectUnloaded</c> notification.</summary>
    public const string ReqnrollProjectUnloaded = "reqnroll/projectUnloaded";
    /// <summary>Method name for the <c>reqnroll/projectFiles</c> notification.</summary>
    public const string ReqnrollProjectFiles = "reqnroll/projectFiles";
    /// <summary>Method name for the <c>reqnroll/findStepUsages</c> request.</summary>
    public const string ReqnrollFindStepUsages = "reqnroll/findStepUsages";
    /// <summary>Method name for the <c>reqnroll/goToStepDefinitions</c> request.</summary>
    public const string ReqnrollGoToStepDefinitions = "reqnroll/goToStepDefinitions";
    /// <summary>Method name for the <c>reqnroll/goToHooks</c> request.</summary>
    public const string ReqnrollGoToHooks = "reqnroll/goToHooks";
    /// <summary>Method name for the <c>reqnroll/goToMatchingScenarios</c> request (issue #373).</summary>
    public const string ReqnrollGoToMatchingScenarios = "reqnroll/goToMatchingScenarios";
    /// <summary>Method name for the <c>reqnroll/resolveTestTargets</c> request (issue #262).</summary>
    public const string ReqnrollResolveTestTargets = "reqnroll/resolveTestTargets";
    /// <summary>Method name for the <c>reqnroll/findUnusedStepDefinitions</c> request.</summary>
    public const string ReqnrollFindUnusedStepDefinitions = "reqnroll/findUnusedStepDefinitions";
    /// <summary>Method name for the <c>reqnroll/renameTargets</c> request.</summary>
    public const string ReqnrollRenameTargets = "reqnroll/renameTargets";
    /// <summary>Method name for the <c>reqnroll/selectRenameTarget</c> notification.</summary>
    public const string ReqnrollSelectRenameTarget = "reqnroll/selectRenameTarget";
    /// <summary>Method name for the <c>reqnroll/refreshCodeLens</c> notification.</summary>
    public const string ReqnrollRefreshCodeLens = "reqnroll/refreshCodeLens";
    /// <summary>Method name for the <c>reqnroll/semanticTokens</c> push notification.</summary>
    public const string ReqnrollSemanticTokens = "reqnroll/semanticTokens";
    /// <summary>Method name for the <c>reqnroll/documentSymbolHierarchical</c> request.</summary>
    public const string ReqnrollDocumentSymbolHierarchical = "reqnroll/documentSymbolHierarchical";
    /// <summary>Method name for the <c>reqnroll/documentActivated</c> notification.</summary>
    public const string ReqnrollDocumentActivated = "reqnroll/documentActivated";

    // ── Standard LSP Methods ────────────────────────────────────────────────
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
    /// <summary>Method name for the <c>workspace/didChangeWorkspaceFolders</c> notification.</summary>
    public const string WorkspaceDidChangeWorkspaceFolders = "workspace/didChangeWorkspaceFolders";
    /// <summary>Method name for the <c>workspace/semanticTokens/refresh</c> request.</summary>
    public const string WorkspaceSemanticTokensRefresh = "workspace/semanticTokens/refresh";
    /// <summary>Method name for the <c>workspace/inlayHint/refresh</c> request.</summary>
    public const string WorkspaceInlayHintRefresh = "workspace/inlayHint/refresh";

    // ── Telemetry ───────────────────────────────────────────────────────────
    /// <summary>Method name for the <c>telemetry/event</c> notification.</summary>
    public const string TelemetryEvent = "telemetry/event";

    // ── Internal Pipeline Operations (not on the wire; perf-recorder labels only) ──
    /// <summary>Internal perf-recorder label for binding-registry reconciliation after a connector update.</summary>
    public const string InternalBindingRegistryReconcile = "internal/bindingRegistryReconcile";
    /// <summary>Internal perf-recorder label for reconciliation triggered by a <c>reqnroll.json</c> change.</summary>
    public const string InternalReqnrollConfigReconcile = "internal/reqnrollConfigReconcile";
    /// <summary>Internal perf-recorder label for a debounced feature-file rescan.</summary>
    public const string InternalFeatureRescan = "internal/featureRescan";
}
