using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Handles what happens after a rename's <see cref="WorkspaceEditBuilder"/> is built: pushing the
/// edit to Visual Studio (which needs a genuine <c>workspace/applyEdit</c> push, unlike other
/// clients), invalidating the match cache for closed <c>.feature</c> files the edit touched, and
/// self-refreshing the C# binding registry for an edited <c>.cs</c> file. Extracted from
/// <see cref="RenameHandler.HandleRenameAsync"/> (issue #139) — these are "what happens after
/// the edit is built" concerns, distinct from "how the edit is built."
/// </summary>
internal sealed class RenamePostApplyCoordinator
{
    private readonly ILanguageServerFacade          _languageServer;
    private readonly ClientIdeContext               _clientIdeContext;
    private readonly IBindingMatchService           _matchService;
    private readonly IDocumentBufferService         _documentBuffer;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly ICSharpFileTextCache           _csharpFileTextCache;
    private readonly IIdeSupportLogger              _logger;

    public RenamePostApplyCoordinator(
        ILanguageServerFacade          languageServer,
        ClientIdeContext               clientIdeContext,
        IBindingMatchService           matchService,
        IDocumentBufferService         documentBuffer,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        ICSharpFileTextCache           csharpFileTextCache,
        IIdeSupportLogger              logger)
    {
        _languageServer          = languageServer;
        _clientIdeContext        = clientIdeContext;
        _matchService            = matchService;
        _documentBuffer          = documentBuffer;
        _csharpDiscoveryService  = csharpDiscoveryService;
        _csharpFileTextCache     = csharpFileTextCache;
        _logger                  = logger;
    }

    /// <summary>
    /// VS's Rename Step command sends <c>textDocument/rename</c> over a custom interception pipe
    /// that swallows this method's return value before VS's built-in LSP client ever sees it, so
    /// VS needs the edit pushed via a genuine <c>workspace/applyEdit</c> request instead — the
    /// same mechanism already proven for the Comment/Uncomment toggle (<c>CommentToggleHandler</c>).
    /// Other clients (e.g. VS Code) apply the returned <see cref="WorkspaceEdit"/> natively and
    /// must NOT also receive this push, or the edit would be applied twice — so this is a no-op
    /// (returns <see langword="true"/> immediately) for every non-VS client.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the edit was applied (or no VS push was needed);
    /// <see langword="false"/> when VS rejected or failed to apply the edit (e.g. a locked/
    /// read-only file, or the user having closed the document with unsaved conflicting changes) —
    /// callers must not touch server-side caches and must reject the rename in that case, since
    /// the actual buffer/file content never changed.
    /// </returns>
    public async Task<bool> PushEditIfVisualStudioAsync(WorkspaceEditBuilder builder, CancellationToken cancellationToken)
    {
        if (!_clientIdeContext.IsVisualStudio)
            return true;

        // VS never advertises changeAnnotationSupport (Phase 0), so builder's edits are
        // already plain TextEdit here — this push is unannotated DocumentChanges regardless.
        var pushParams = new ApplyWorkspaceEditParams
        {
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    builder.GetEditsByUri().Select(kvp => new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = kvp.Key, Version = null },
                        Edits = new TextEditContainer(kvp.Value)
                    })))
            }
        };

        var response = await _languageServer.SendRequest(LspMethodNames.WorkspaceApplyEdit, pushParams)
            .Returning<ApplyWorkspaceEditResponse>(cancellationToken);

        if (response is not { Applied: true })
        {
            _logger.LogVerbose($"RenamePostApplyCoordinator: VS rejected workspace/applyEdit (reason: '{response?.FailureReason}') — not refreshing server caches");
            return false;
        }

        _logger.LogVerbose("RenamePostApplyCoordinator: VS applied workspace/applyEdit");
        return true;
    }

    /// <summary>
    /// Invalidates the match cache for CLOSED feature files that were modified by the rename.
    /// When a feature file is closed at rename time, no didChange notification fires, so the
    /// server's in-memory match cache would otherwise retain the old step text until the file is
    /// re-opened and re-parsed. For OPEN files, applying the edit (via workspace/applyEdit for VS,
    /// or natively for other clients) already triggers a real textDocument/didChange, which
    /// reparses and correctly rebuilds the match cache through the normal sync pipeline —
    /// invalidating here too would race with that rebuild. Losing that race (which happens
    /// reliably, since this runs after awaiting the VS applyEdit round-trip) leaves the cache
    /// empty with nothing left to repopulate it, since the file's content isn't changing again:
    /// confirmed live as inlay hints silently disappearing for the whole file post-rename.
    /// </summary>
    public void InvalidateClosedFeatureCaches(WorkspaceEditBuilder builder)
    {
        foreach (var changedUri in builder.TouchedUris)
        {
            var changedPath = changedUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(changedPath) && changedPath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) &&
                !_documentBuffer.TryGet(changedUri, out _))
            {
                _matchService.InvalidateAllForDocument(changedUri.ToString());
                _logger.LogVerbose($"RenamePostApplyCoordinator: invalidated match cache for closed '{changedUri}'");
            }
        }
    }

    /// <summary>
    /// Self-refreshes the C# binding registry for the edited .cs file directly, rather than
    /// relying on a client-echoed textDocument/didChange (there is no file-system watcher for .cs
    /// content changes, and a closed file may never round-trip one at all). For VS Code (no
    /// confirmed-apply signal available) this is optimistic, same as the .feature invalidation
    /// above; for VS it only runs once workspace/applyEdit has been confirmed applied. Any
    /// redundant didChange-triggered refresh the client's own sync machinery fires afterward is
    /// harmless (idempotent — same content, same result). No-op when <paramref name="csFileUri"/>
    /// or <paramref name="newCsText"/> is <see langword="null"/> (no .cs edit was built).
    /// </summary>
    public async Task RefreshCSharpRegistryAsync(DocumentUri? csFileUri, string? newCsText, CancellationToken cancellationToken)
    {
        if (csFileUri is null || newCsText == null)
            return;

        await _csharpDiscoveryService.UpdateFromSourceAsync(csFileUri, newCsText, isOpen: false, cancellationToken);
        _csharpFileTextCache.Update(csFileUri, newCsText);
        _logger.LogVerbose($"RenamePostApplyCoordinator: self-refreshed C# binding registry for '{csFileUri}'");
    }
}
