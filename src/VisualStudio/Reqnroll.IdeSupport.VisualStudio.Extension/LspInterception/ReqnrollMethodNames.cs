namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Centralizes the custom <c>reqnroll/*</c> LSP method names the Visual Studio extension sends or
/// receives (issue #682) -- the VS-side counterpart to the server's own
/// <c>LspMethodNames</c> (<c>src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs</c>),
/// VS Code's <c>ReqnrollMethods</c> (<c>src/VSCode/src/lsp/lspMethods.ts</c>), and Rider's
/// <c>@JsonRequest</c>/<c>@JsonNotification</c>-annotated <c>ReqnrollLanguageServer</c> interface.
/// Standard LSP methods (<c>textDocument/*</c>, <c>workspace/*</c>, etc.) are out of scope here --
/// this file is custom Reqnroll extensions only.
/// </summary>
/// <remarks>
/// Every server-defined <c>reqnroll/*</c> method has a constant here except
/// <c>reqnroll/renameApplied</c>: VS never sends it by design (see
/// <c>RenamePostApplyCoordinator.cs</c>'s <c>IsVisualStudio</c> branch), so it would be dead code
/// added purely for completeness.
/// <para>
/// Two look-alikes are deliberately excluded: <see cref="FindStepUsages.FeatureReferencesDataSource"/>'s
/// and <see cref="FindUnusedStepDefinitions.UnusedStepDefinitionsDataSource"/>'s
/// <c>SourceTypeIdentifier</c> values (<c>"reqnroll/findReferences"</c>,
/// <c>"reqnroll/unusedStepDefinitions"</c>) are VS Find Results window source-type identifiers,
/// never serialized over the wire -- they just happen to share the <c>reqnroll/</c> prefix by
/// convention. Do not fold them into this registry.
/// </para>
/// </remarks>
internal static class ReqnrollMethodNames
{
    /// <summary>Method name for the <c>reqnroll/projectLoaded</c> notification.</summary>
    public const string ProjectLoaded = "reqnroll/projectLoaded";
    /// <summary>Method name for the <c>reqnroll/projectUnloaded</c> notification.</summary>
    public const string ProjectUnloaded = "reqnroll/projectUnloaded";
    /// <summary>Method name for the <c>reqnroll/projectFiles</c> notification.</summary>
    public const string ProjectFiles = "reqnroll/projectFiles";
    /// <summary>Method name for the <c>reqnroll/findStepUsages</c> request.</summary>
    public const string FindStepUsages = "reqnroll/findStepUsages";
    /// <summary>Method name for the <c>reqnroll/goToHooks</c> request.</summary>
    public const string GoToHooks = "reqnroll/goToHooks";
    /// <summary>Method name for the <c>reqnroll/goToMatchingScenarios</c> request (issue #373).</summary>
    public const string GoToMatchingScenarios = "reqnroll/goToMatchingScenarios";
    /// <summary>Method name for the <c>reqnroll/resolveTestTargets</c> request (issue #262).</summary>
    public const string ResolveTestTargets = "reqnroll/resolveTestTargets";
    /// <summary>Method name for the <c>reqnroll/findUnusedStepDefinitions</c> request.</summary>
    public const string FindUnusedStepDefinitions = "reqnroll/findUnusedStepDefinitions";
    /// <summary>Method name for the <c>reqnroll/renameTargets</c> request.</summary>
    public const string RenameTargets = "reqnroll/renameTargets";
    /// <summary>Method name for the <c>reqnroll/selectRenameTarget</c> notification.</summary>
    public const string SelectRenameTarget = "reqnroll/selectRenameTarget";
    /// <summary>Method name for the <c>reqnroll/refreshCodeLens</c> notification.</summary>
    public const string RefreshCodeLens = "reqnroll/refreshCodeLens";
    /// <summary>Method name for the <c>reqnroll/semanticTokens</c> push notification.</summary>
    public const string SemanticTokens = "reqnroll/semanticTokens";
    /// <summary>Method name for the <c>reqnroll/documentSymbolHierarchical</c> request.</summary>
    public const string DocumentSymbolHierarchical = "reqnroll/documentSymbolHierarchical";
    /// <summary>Method name for the <c>reqnroll/documentActivated</c> notification.</summary>
    public const string DocumentActivated = "reqnroll/documentActivated";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/registerRun</c> request (LSP-server outcome pipeline).</summary>
    public const string RegisterTestRun = "reqnroll/testOutcomes/registerRun";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/getOutcome</c> request (LSP-server outcome pipeline).</summary>
    public const string GetTestOutcome = "reqnroll/testOutcomes/getOutcome";
    /// <summary>Method name for the <c>reqnroll/testOutcomes/changed</c> push notification (LSP-server outcome pipeline).</summary>
    public const string TestOutcomesChanged = "reqnroll/testOutcomes/changed";
}
