namespace Reqnroll.IdeSupport.Common.Lsp;

/// <summary>
/// Centralizes the custom <c>reqnroll/*</c> LSP method names shared by the LSP server and the
/// Visual Studio extension (issue #682) -- the counterpart to
/// <see cref="Common.Lsp.LspStandardMethodNames"/> for standard methods, VS Code's
/// <c>ReqnrollMethods</c> (<c>src/VSCode/src/lsp/lspMethods.ts</c>), and Rider's
/// <c>@JsonRequest</c>/<c>@JsonNotification</c>-annotated <c>ReqnrollLanguageServer</c> interface.
/// Standard LSP methods (<c>textDocument/*</c>, <c>workspace/*</c>, etc.) are out of scope here --
/// this file is custom Reqnroll extensions only. See <see cref="Common.Lsp.LspStandardMethodNames"/> for the
/// standard methods the extension uses.
/// </summary>
/// <remarks>
/// Every server-defined <c>reqnroll/*</c> method has a constant here except
/// <c>reqnroll/renameApplied</c>: VS never sends it by design (see
/// <c>RenamePostApplyCoordinator.cs</c>'s <c>IsVisualStudio</c> branch), so it would be dead code
/// added purely for completeness.
/// <para>
/// Two look-alikes are deliberately excluded: <see cref="FindStepUsages.FeatureReferencesDataSource"/>'s
/// and <see cref="FindUnusedStepDefinitions.StepDefinitionsDataSource"/>'s
/// <c>SourceTypeIdentifier</c> values (<c>"reqnroll/findReferences"</c>,
/// <c>"reqnroll/stepDefinitions"</c>) are VS Find Results window source-type identifiers,
/// never serialized over the wire -- they just happen to share the <c>reqnroll/</c> prefix by
/// convention. Do not fold them into this registry.
/// </para>
/// </remarks>
public static class CustomLspMethodNames
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

}
