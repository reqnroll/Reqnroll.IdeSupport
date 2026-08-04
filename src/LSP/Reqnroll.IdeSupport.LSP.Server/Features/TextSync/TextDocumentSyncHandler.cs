using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

/// <summary>
/// Single OmniSharp text-document sync handler covering both document types the server cares
/// about: <c>.feature</c> files (parsed into the Gherkin document buffer) and <c>.cs</c> files
/// (fed to Roslyn/C# source-level binding discovery). Both are registered
/// here so that a single sync handler owns text synchronization; the per-document branching is
/// done by file extension.
/// </summary>
public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IBindingMatchService _bindingMatchService;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly ICSharpFileTextCache _csharpFileTextCache;
    private readonly IMediator _mediator;
    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    private static readonly TextDocumentSelector _documentSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" },
        // The server registers interest in .cs files only to drive Roslyn binding re-discovery;
        // it does not provide general C# language features. See design doc §5 "Document Scope".
        new TextDocumentFilter { Pattern = "**/*.cs" }
    );

    /// <summary>Initializes a new instance of the <see cref="TextDocumentSyncHandler"/> class.</summary>
    public TextDocumentSyncHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        IBindingMatchService bindingMatchService,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        ICSharpFileTextCache csharpFileTextCache,
        IMediator mediator,
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _taggerService = taggerService;
        _bindingMatchService = bindingMatchService;
        _csharpDiscoveryService = csharpDiscoveryService;
        _csharpFileTextCache = csharpFileTextCache;
        _mediator = mediator;
        _languageServer = languageServer;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Reports the language id (<c>"csharp"</c> or <c>"Gherkin"</c>) the server should associate with the given document URI.</summary>
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
        => new(uri, IsCSharp(uri) ? "csharp" : "Gherkin");

    /// <summary>Builds the LSP registration options for text synchronization: full-document change events and open/close notifications, without save text.</summary>
    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = _documentSelector,
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };

    /// <summary>Handles <c>textDocument/didOpen</c>: for C# files, updates the live-text cache and runs Roslyn binding discovery; for Gherkin files, updates the document buffer and re-parses/republishes diagnostics.</summary>
    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var version = request.TextDocument.Version;
        var text = request.TextDocument.Text;

        // Performance Verification (Layer 4): the trigger side of every downstream operation.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentDidOpen, uri);

        if (IsCSharp(uri))
        {
            _logger.LogInfo($"C# document opened: {uri} (version {version})");
            _csharpFileTextCache.Update(uri, text);
            await _csharpDiscoveryService.UpdateFromSourceAsync(uri, text, true, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }

        _logger.LogInfo($"Document opened: {uri} (version {version})");
        _documentBufferService.Update(uri, version, text);

        await ParseAndNotifyAsync(uri, version, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>Handles <c>textDocument/didChange</c>: for C# files, feeds the full changed text into Roslyn binding discovery; for Gherkin files, updates the document buffer and re-parses/republishes diagnostics.</summary>
    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var version = request.TextDocument.Version;

        // With TextDocumentSyncKind.Full the single change contains the full document text.
        var text = request.ContentChanges.LastOrDefault()?.Text ?? string.Empty;

        // Performance Verification (Layer 4): the trigger side of every downstream operation
        // (only the diagnostics-publish half was measured before issue #113).
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentDidChange, uri);

        if (IsCSharp(uri))
        {
            _logger.LogInfo($"C# document changed: {uri} (version {version})");
            _csharpFileTextCache.Update(uri, text);
            await _csharpDiscoveryService.UpdateFromSourceAsync(uri, text, false, cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }

        _logger.LogInfo($"Document changed: {uri} (version {version})");
        _documentBufferService.Update(uri, version, text);

        await ParseAndNotifyAsync(uri, version, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>Handles <c>textDocument/didSave</c>. With full-document sync the buffer is already current from the preceding <c>didChange</c>, so this is currently a no-op beyond logging.</summary>
    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInfo($"Document saved: {request.TextDocument.Uri}");
        // Re-parse on save when text is not sent on change (e.g., incremental sync scenarios).
        // With full sync the buffer is already up to date; nothing extra needed.
        return Unit.Task;
    }

    /// <summary>Handles <c>textDocument/didClose</c>: drops the cached live text for C# files (disk becomes the source of truth again) while leaving their last-discovered bindings intact until a rebuild supersedes them.</summary>
    public override async Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentDidClose, uri);

        // .cs files are not tracked in the Gherkin document buffer; their last-discovered bindings
        // are intentionally retained after close (a rebuild, not a close, supersedes them). Their
        // cached live text is the opposite: once closed, disk is the source of truth again (VS
        // prompts to save or discard before a close reaches the server), so drop it here rather
        // than risk later reads preferring a stale, possibly-discarded in-memory copy over disk.
        if (IsCSharp(uri))
        {
            _csharpFileTextCache.Remove(uri);
            return Unit.Value;
        }

        _logger.LogInfo($"Document closed: {uri}");
        _documentBufferService.Remove(uri);
        _bindingMatchService.InvalidateAllForDocument(uri.ToString());

        // Repopulate the match cache from disk so the file's usages remain discoverable by
        // workspace-wide features (Find Usages / Rename) while it is closed. Without this, the
        // file would vanish from the cache until the next full registry replacement, so a rename
        // driven from a .cs file would silently skip closed feature files. Reading from disk also
        // makes the cached match set reflect the persisted content (e.g. if edits were discarded).
        await _taggerService.RescanClosedFileAsync(uri).ConfigureAwait(false);

        // Clear any squiggles the IDE may have retained for this URI.
        // LSP spec: sending an empty diagnostics list for a URI clears all previously
        // pushed diagnostics for that URI on the client.
        _languageServer.SendNotification(
            LspMethodNames.TextDocumentPublishDiagnostics,
            new PublishDiagnosticsParams
            {
                Uri         = uri,
                Diagnostics = new Container<Diagnostic>()
            });

        return Unit.Value;
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ParseAndNotifyAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        // ParseAsync stores updated tags, recomputes/stores the binding match set, and
        // invalidates the semantic token cache internally before this notification fires.
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }
}
