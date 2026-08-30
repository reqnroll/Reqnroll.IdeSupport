using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.InlayHints;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.InlayHints;

/// <summary>
/// Handles <c>textDocument/inlayHint</c> for <c>.feature</c> files (F23 — binding info hints).
/// Shows the bound step definition's method name at the end of each step line, with the full
/// signature in the hint's tooltip. Ambiguous steps show a match count instead; a Scenario
/// Outline/Background step whose example rows resolve to more than one distinct binding shows a
/// binding count. Undefined steps get no hint — the diagnostic already covers those.
/// </summary>
/// <remarks>
/// Registered manually (see <c>LanguageServerOptionsExtensions.InitializeCustomProtocolRouting</c>)
/// with <c>inlayHintProvider</c> declared statically in the initialize response (see
/// <c>Program.ConfigureServer</c>), instead of via OmniSharp's dynamic-registration handler
/// interface. vscode-languageclient's dynamic <c>client/registerCapability</c> round trip for
/// inlayHint/foldingRange races VS Code's restore of previously-open <c>.feature</c> tabs on
/// window load — if the tab renders first, VS Code never re-checks for a provider for the rest
/// of the session. Static declaration removes the race entirely.
/// </remarks>
public sealed class InlayHintHandler
{
    private readonly IBindingMatchService      _matchService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IInlayHintService  _hintService;
    private readonly IIdeSupportLogger           _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="InlayHintHandler"/> class.</summary>
    public InlayHintHandler(
        IBindingMatchService      matchService,
        ILspWorkspaceScopeManager scopeManager,
        IInlayHintService  hintService,
        IIdeSupportLogger           logger,
        IOperationDurationRecorder? recorder = null)
    {
        _matchService = matchService;
        _scopeManager = scopeManager;
        _hintService  = hintService;
        _logger       = logger;
        _recorder     = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>textDocument/inlayHint</c> request for binding-info inlay hints.</summary>
    public Task<InlayHintContainer?> HandleAsync(InlayHintParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): fires per visible range on scroll — frequent, was uninstrumented.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentInlayHint, uri);

        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var matchKey = primaryOwner is not null
            ? new MatchSetKey(uri.ToString(), new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker))
            : MatchSetKey.ForUnknownProject(uri.ToString());

        if (!_matchService.TryGet(matchKey, out var matchSet) || matchSet is null)
        {
            _logger.LogVerbose($"InlayHintHandler: no match set cached for {uri}");
            return Task.FromResult<InlayHintContainer?>(new InlayHintContainer());
        }

        // An end position at column 0 means "the start of that line", so that line is outside the
        // requested range (LSP convention). Same adjustment SemanticTokensService applies for
        // textDocument/semanticTokens/range — the two range-scoped requests must agree on what an
        // exclusive end position means. The Intersects filter below would also drop those hints,
        // but only after Build had already computed them.
        var endLine = request.Range.End.Character == 0 && request.Range.End.Line > 0
            ? request.Range.End.Line - 1
            : request.Range.End.Line;

        var hints = _hintService.Build(matchSet, request.Range.Start.Line, endLine)
            .Select(ToInlayHint)
            .Where(h => Intersects(h.Position, request.Range))
            .ToList();

        _logger.LogVerbose($"InlayHintHandler: {hints.Count} hint(s) for {uri}");
        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    private static InlayHint ToInlayHint(GherkinInlayHint hint)
    {
        var position = hint.AnchorRange.ToLspRange().End;
        return new InlayHint
        {
            Position     = position,
            // The implicit string conversion returns StringOrInlayHintLabelParts? (it also
            // accepts null input), which trips a nullable warning against this non-nullable
            // property; the explicit constructor sidesteps that.
            Label        = new StringOrInlayHintLabelParts(hint.Label),
            Kind         = InlayHintKind.Type,
            Tooltip      = hint.Tooltip,
            PaddingLeft  = true,
        };
    }

    /// <summary>Whether the (single-point) hint position falls within the requested viewport.</summary>
    private static bool Intersects(Position position, LspRange range)
    {
        var afterStart = position.Line > range.Start.Line ||
            (position.Line == range.Start.Line && position.Character >= range.Start.Character);
        var beforeEnd = position.Line < range.End.Line ||
            (position.Line == range.End.Line && position.Character <= range.End.Character);
        return afterStart && beforeEnd;
    }
}
