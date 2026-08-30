using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Folding;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Folding;

/// <summary>
/// Handles <c>textDocument/foldingRange</c> for <c>.feature</c> files (Code Folding).
/// Returns foldable regions for Feature bodies, Scenario/Background/Rule blocks,
/// Doc strings, Data tables, and Examples blocks.
/// </summary>
/// <remarks>
/// Registered manually (see <c>LanguageServerOptionsExtensions.InitializeCustomProtocolRouting</c>)
/// with <c>foldingRangeProvider</c> declared statically in the initialize response (see
/// <c>Program.ConfigureServer</c>), instead of via OmniSharp's dynamic-registration handler
/// interface. vscode-languageclient's dynamic <c>client/registerCapability</c> round trip for
/// inlayHint/foldingRange races VS Code's restore of previously-open <c>.feature</c> tabs on
/// window load — if the tab renders first, VS Code never re-checks for a provider for the rest
/// of the session. Static declaration removes the race entirely.
/// </remarks>
public sealed class FoldingRangeHandler
{
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IFoldingRangeService    _foldingService;
    private readonly IFeatureParseCoordinator      _parseCoordinator;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder     _recorder;

    /// <summary>Initializes a new instance of the <see cref="FoldingRangeHandler"/> class.</summary>
    public FoldingRangeHandler(
        IDocumentBufferService documentBufferService,
        IFoldingRangeService foldingService,
        IFeatureParseCoordinator parseCoordinator,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _foldingService         = foldingService;
        _parseCoordinator      = parseCoordinator;
        _logger                = logger;
        _recorder              = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>textDocument/foldingRange</c> request for feature-file folding regions.</summary>
    public async Task<Container<FoldingRange>?> HandleAsync(
        FoldingRangeRequestParam request, CancellationToken ct)
    {
        // Benchmarked as load-only in the synthetic harness (no published target); now also
        // has field visibility so a real P95 bar can be set.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentFoldingRange, request.TextDocument.Uri);

        _logger.LogInfo($"Code Folding textDocument/foldingRange: {request.TextDocument.Uri}");

        // foldingRange has no LSP refresh capability (issue #471) -- unlike codeLens/semanticTokens/
        // inlayHint, a stale/empty answer here can never be corrected by the server later, so this
        // must wait for any parse the sync handler handed off to IFeatureParseCoordinator instead
        // of reading buffer.Tags out from under it.
        await _parseCoordinator.WaitForReadyAsync(request.TextDocument.Uri, ct).ConfigureAwait(false);

        if (!_documentBufferService.TryGet(request.TextDocument.Uri, out var buffer) || buffer?.Tags is null)
            return new Container<FoldingRange>();

        var ranges = _foldingService.BuildFoldingRanges(buffer.Tags);
        if (ranges.Count == 0)
            return new Container<FoldingRange>();

        return new Container<FoldingRange>(ranges.Select(ToFoldingRange));
    }

    // ── Conversion helpers ────────────────────────────────────────────────

    private static FoldingRange ToFoldingRange(GherkinFoldingRange r)
    {
        if (r.Kind.HasValue)
        {
            var lspKind = r.Kind.Value switch
            {
                GherkinFoldingRangeKind.Comment => FoldingRangeKind.Comment,
                GherkinFoldingRangeKind.Imports => FoldingRangeKind.Imports,
                GherkinFoldingRangeKind.Region  => FoldingRangeKind.Region,
                _                               => (FoldingRangeKind?)null,
            };
            return new FoldingRange
            {
                StartLine = r.StartLine,
                EndLine   = r.EndLine,
                Kind      = lspKind,
            };
        }

        return new FoldingRange
        {
            StartLine = r.StartLine,
            EndLine   = r.EndLine,
        };
    }
}
