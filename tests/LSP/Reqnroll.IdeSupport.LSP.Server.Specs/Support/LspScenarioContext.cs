using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Per-scenario state shared between step classes via Reqnroll's container.
/// Owns the <see cref="LspServerHarness"/> and a temporary workspace folder.
/// </summary>
public sealed class LspScenarioContext
{
    public LspScenarioContext()
    {
        WorkspaceFolder = Path.Combine(Path.GetTempPath(), "ReqnrollLspSpecs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceFolder);
    }

    public LspServerHarness Harness { get; } = new();
    public string WorkspaceFolder { get; }
    public bool Started { get; set; }

    // Tracking for the most recently opened document, used by Then-steps.
    public DocumentUri? LastUri { get; set; }
    public string LastDocumentText { get; set; } = string.Empty;
    public int LastVersion { get; set; }
    public SemanticTokens? LastTokens { get; set; }
    public LocationOrLocationLinks? LastReferences { get; set; }
    public FindStepUsagesResponse? LastFindStepUsages { get; set; }
    public GoToHooksResponse? LastGoToHooks { get; set; }
    public CodeLens[]? LastCodeLens { get; set; }
    public CompletionList? LastCompletions { get; set; }
    public TextEdit[]? LastFormattingEdits { get; set; }
    public SymbolInformationOrDocumentSymbolContainer? LastDocumentSymbols { get; set; }
    public Container<FoldingRange>? LastFoldingRanges { get; set; }
    public ApplyWorkspaceEditParams? LastToggleEdit { get; set; }

    // F15 — Find Unused Step Definitions
    public FindUnusedStepDefinitionsResponse? LastFindUnused { get; set; }

    // F16 — Step Rename
    public WorkspaceEdit? LastRenameEdit { get; set; }
    public RenameTargetsResponse? LastRenameTargets { get; set; }
    public OmniSharp.Extensions.LanguageServer.Protocol.Models.RangeOrPlaceholderRange? LastPrepareRenameRange { get; set; }

    // F6 — Define Steps (code actions)
    public CommandOrCodeActionContainer? LastCodeActions { get; set; }

    public DocumentUri UriFor(string relativeName)
        => DocumentUri.FromFileSystemPath(Path.Combine(WorkspaceFolder, relativeName));

    public async Task EnsureStartedAsync(string? ideId = null, bool supportsChangeAnnotations = false)
    {
        if (Started) return;
        await Harness.StartAsync(WorkspaceFolder, ideId, supportsChangeAnnotations).ConfigureAwait(false);
        Started = true;
    }

    public async Task DisposeAsync()
    {
        await Harness.DisposeAsync().ConfigureAwait(false);
        try { if (Directory.Exists(WorkspaceFolder)) Directory.Delete(WorkspaceFolder, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
