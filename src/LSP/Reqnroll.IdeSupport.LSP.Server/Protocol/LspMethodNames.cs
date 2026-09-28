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
    /// <summary>Method name for the <c>reqnroll/findHooks</c> request.</summary>
    public const string ReqnrollFindHooks = "reqnroll/findHooks";
    /// <summary>Method name for the <c>reqnroll/findStepDefinitions</c> request (issue #757).</summary>
    public const string ReqnrollFindStepDefinitions = "reqnroll/findStepDefinitions";
    /// <summary>Method name for the <c>reqnroll/findMatchingScenarios</c> request (issue #373).</summary>
    public const string ReqnrollFindMatchingScenarios = "reqnroll/findMatchingScenarios";
    /// <summary>Method name for the <c>reqnroll/resolveTestTargets</c> request (issue #262).</summary>
    public const string ReqnrollResolveTestTargets = "reqnroll/resolveTestTargets";
    /// <summary>Method name for the <c>reqnroll/resolveContainerTestTargets</c> request — resolves every scenario/Outline under a <c>Feature:</c> or <c>Rule:</c> block in one call (issue #744, "Run scenarios").</summary>
    public const string ReqnrollResolveContainerTestTargets = "reqnroll/resolveContainerTestTargets";
    /// <summary>Method name for the <c>reqnroll/findUnusedStepDefinitions</c> request.</summary>
    public const string ReqnrollFindUnusedStepDefinitions = "reqnroll/findUnusedStepDefinitions";
    /// <summary>Method name for the <c>reqnroll/renameTargets</c> request.</summary>
    public const string ReqnrollRenameTargets = "reqnroll/renameTargets";
    /// <summary>Method name for the <c>reqnroll/selectRenameTarget</c> notification.</summary>
    public const string ReqnrollSelectRenameTarget = "reqnroll/selectRenameTarget";
    /// <summary>Method name for the <c>reqnroll/renameApplied</c> notification.</summary>
    public const string ReqnrollRenameApplied = "reqnroll/renameApplied";
    /// <summary>Method name for the <c>reqnroll/refreshCodeLens</c> notification.</summary>
    public const string ReqnrollRefreshCodeLens = "reqnroll/refreshCodeLens";
    /// <summary>Method name for the <c>reqnroll/semanticTokens</c> push notification.</summary>
    public const string ReqnrollSemanticTokens = "reqnroll/semanticTokens";
    /// <summary>Method name for the <c>reqnroll/documentSymbolHierarchical</c> request.</summary>
    public const string ReqnrollDocumentSymbolHierarchical = "reqnroll/documentSymbolHierarchical";
    /// <summary>Method name for the <c>reqnroll/documentActivated</c> notification.</summary>
    public const string ReqnrollDocumentActivated = "reqnroll/documentActivated";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/registerRun</c> request (LSP-server outcome pipeline).</summary>
    public const string ReqnrollRegisterTestRun = "reqnroll/testOutcomes/registerRun";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/getOutcome</c> request (LSP-server outcome pipeline).</summary>
    public const string ReqnrollGetTestOutcome = "reqnroll/testOutcomes/getOutcome";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/changed</c> push notification (LSP-server outcome pipeline).</summary>
    public const string ReqnrollTestOutcomesChanged = "reqnroll/testOutcomes/changed";

    // ── Standard LSP Methods ────────────────────────────────────────────────
        // These constants delegate to Reqnroll.IdeSupport.Common.Lsp.LspStandardMethodNames
        // so there is a single source of truth shared with the VS extension.
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensFull"/>
        public const string TextDocumentSemanticTokensFull = Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensFull;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensFullDelta"/>
        public const string TextDocumentSemanticTokensFullDelta = Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensFullDelta;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensRange"/>
        public const string TextDocumentSemanticTokensRange = Common.Lsp.LspStandardMethodNames.TextDocumentSemanticTokensRange;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentCompletion"/>
        public const string TextDocumentCompletion = Common.Lsp.LspStandardMethodNames.TextDocumentCompletion;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentDefinition"/>
        public const string TextDocumentDefinition = Common.Lsp.LspStandardMethodNames.TextDocumentDefinition;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentReferences"/>
        public const string TextDocumentReferences = Common.Lsp.LspStandardMethodNames.TextDocumentReferences;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentCodeLens"/>
        public const string TextDocumentCodeLens = Common.Lsp.LspStandardMethodNames.TextDocumentCodeLens;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.CodeLensResolve"/>
        public const string CodeLensResolve = Common.Lsp.LspStandardMethodNames.CodeLensResolve;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentInlayHint"/>
        public const string TextDocumentInlayHint = Common.Lsp.LspStandardMethodNames.TextDocumentInlayHint;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentFoldingRange"/>
        public const string TextDocumentFoldingRange = Common.Lsp.LspStandardMethodNames.TextDocumentFoldingRange;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentPrepareRename"/>
        public const string TextDocumentPrepareRename = Common.Lsp.LspStandardMethodNames.TextDocumentPrepareRename;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentRename"/>
        public const string TextDocumentRename = Common.Lsp.LspStandardMethodNames.TextDocumentRename;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentPublishDiagnostics"/>
        public const string TextDocumentPublishDiagnostics = Common.Lsp.LspStandardMethodNames.TextDocumentPublishDiagnostics;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentFormatting"/>
        public const string TextDocumentFormatting = Common.Lsp.LspStandardMethodNames.TextDocumentFormatting;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentRangeFormatting"/>
        public const string TextDocumentRangeFormatting = Common.Lsp.LspStandardMethodNames.TextDocumentRangeFormatting;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentOnTypeFormatting"/>
        public const string TextDocumentOnTypeFormatting = Common.Lsp.LspStandardMethodNames.TextDocumentOnTypeFormatting;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentCodeAction"/>
        public const string TextDocumentCodeAction = Common.Lsp.LspStandardMethodNames.TextDocumentCodeAction;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentDocumentSymbol"/>
        public const string TextDocumentDocumentSymbol = Common.Lsp.LspStandardMethodNames.TextDocumentDocumentSymbol;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentDidOpen"/>
        public const string TextDocumentDidOpen = Common.Lsp.LspStandardMethodNames.TextDocumentDidOpen;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentDidChange"/>
        public const string TextDocumentDidChange = Common.Lsp.LspStandardMethodNames.TextDocumentDidChange;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TextDocumentDidClose"/>
        public const string TextDocumentDidClose = Common.Lsp.LspStandardMethodNames.TextDocumentDidClose;

        // ── Workspace Methods ───────────────────────────────────────────────────
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceApplyEdit"/>
        public const string WorkspaceApplyEdit = Common.Lsp.LspStandardMethodNames.WorkspaceApplyEdit;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceCodeLensRefresh"/>
        public const string WorkspaceCodeLensRefresh = Common.Lsp.LspStandardMethodNames.WorkspaceCodeLensRefresh;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceDidChangeWatchedFiles"/>
        public const string WorkspaceDidChangeWatchedFiles = Common.Lsp.LspStandardMethodNames.WorkspaceDidChangeWatchedFiles;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceExecuteCommand"/>
        public const string WorkspaceExecuteCommand = Common.Lsp.LspStandardMethodNames.WorkspaceExecuteCommand;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceDidChangeWorkspaceFolders"/>
        public const string WorkspaceDidChangeWorkspaceFolders = Common.Lsp.LspStandardMethodNames.WorkspaceDidChangeWorkspaceFolders;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceSemanticTokensRefresh"/>
        public const string WorkspaceSemanticTokensRefresh = Common.Lsp.LspStandardMethodNames.WorkspaceSemanticTokensRefresh;
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.WorkspaceInlayHintRefresh"/>
        public const string WorkspaceInlayHintRefresh = Common.Lsp.LspStandardMethodNames.WorkspaceInlayHintRefresh;

        // ── Telemetry ───────────────────────────────────────────────────────────
        /// <inheritdoc cref="Common.Lsp.LspStandardMethodNames.TelemetryEvent"/>
        public const string TelemetryEvent = Common.Lsp.LspStandardMethodNames.TelemetryEvent;

    // ── Internal Pipeline Operations (not on the wire; perf-recorder labels only) ──
    /// <summary>Internal perf-recorder label for binding-registry reconciliation after a connector update.</summary>
    public const string InternalBindingRegistryReconcile = "internal/bindingRegistryReconcile";
    /// <summary>Internal perf-recorder label for reconciliation triggered by a <c>reqnroll.json</c> change.</summary>
    public const string InternalReqnrollConfigReconcile = "internal/reqnrollConfigReconcile";
    /// <summary>Internal perf-recorder label for a debounced feature-file rescan.</summary>
    public const string InternalFeatureRescan = "internal/featureRescan";
    /// <summary>
    /// Internal perf-recorder label for a rename's post-response apply: the Visual Studio
    /// <c>workspace/applyEdit</c> round trip plus the cache commit it confirms. This work used to
    /// sit inside the measured <c>textDocument/rename</c> request; since issue #671 (R1) it runs
    /// after that response, so without its own label its cost would not appear anywhere.
    /// </summary>
    public const string InternalRenamePostResponseApply = "internal/renamePostResponseApply";
}
