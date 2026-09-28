namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Centralizes the standard LSP <c>textDocument/*</c> and <c>workspace/*</c> method names the
/// Visual Studio extension sends or intercepts -- the standard-methods counterpart to
/// <see cref="ReqnrollMethodNames"/>, which covers only the custom <c>reqnroll/*</c> methods.
/// </summary>
/// <remarks>
/// The server's own <c>LspMethodNames</c>
/// (<c>src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs</c>) already centralizes
/// both standard and custom method names, but the VS extension cannot compile against it: its
/// <c>ProjectReference</c> to LSP.Server is <c>ReferenceOutputAssembly=false</c> (VSIX bundling
/// only, not a compile reference), and CI builds with <c>UseExternalLspServerBuild=true</c>, which
/// drops that reference entirely. This class exists so the VS extension has its own copy of the
/// handful of standard method names it actually uses, instead of scattering them as string
/// literals or per-service <c>private const</c>s.
/// <para>
/// Only the methods the VS extension itself sends or intercepts are listed here -- this is not a
/// full mirror of the server's <c>LspMethodNames</c>.
/// </para>
/// </remarks>
internal static class LspStandardMethodNames
{
    /// <summary>Method name for the <c>textDocument/didOpen</c> notification.</summary>
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    /// <summary>Method name for the <c>textDocument/didChange</c> notification.</summary>
    public const string TextDocumentDidChange = "textDocument/didChange";
    /// <summary>Method name for the <c>textDocument/didClose</c> notification.</summary>
    public const string TextDocumentDidClose = "textDocument/didClose";
    /// <summary>Method name for the <c>textDocument/codeLens</c> request.</summary>
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    /// <summary>Method name for the <c>textDocument/rename</c> request.</summary>
    public const string TextDocumentRename = "textDocument/rename";
    /// <summary>Method name for the <c>textDocument/formatting</c> request.</summary>
    public const string TextDocumentFormatting = "textDocument/formatting";
    /// <summary>Method name for the <c>textDocument/rangeFormatting</c> request.</summary>
    public const string TextDocumentRangeFormatting = "textDocument/rangeFormatting";
    /// <summary>Method name for the <c>textDocument/semanticTokens/full</c> request.</summary>
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    /// <summary>Method name for the <c>textDocument/semanticTokens/full/delta</c> request.</summary>
    public const string TextDocumentSemanticTokensFullDelta = "textDocument/semanticTokens/full/delta";
    /// <summary>Method name for the <c>textDocument/semanticTokens/range</c> request.</summary>
    public const string TextDocumentSemanticTokensRange = "textDocument/semanticTokens/range";
    /// <summary>Method name for the <c>workspace/executeCommand</c> request.</summary>
    public const string WorkspaceExecuteCommand = "workspace/executeCommand";
}
