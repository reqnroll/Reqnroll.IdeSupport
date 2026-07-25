using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

namespace Reqnroll.IdeSupport.LSP.Server.Features.DocumentOutline;

/// <summary>
/// Handles <c>textDocument/documentSymbol</c> for <c>.feature</c> files (Document Outline).
/// Returns a nested <see cref="DocumentSymbol"/> hierarchy (Feature → Background / Rule →
/// Scenario / Scenario Outline → Step / Examples) when the client declared
/// <c>hierarchicalDocumentSymbolSupport</c>, or a flattened <see cref="SymbolInformation"/> list
/// otherwise (see remarks).
/// </summary>
/// <remarks>
/// Visual Studio's own LSP client does not declare <c>hierarchicalDocumentSymbolSupport</c> in its
/// <c>documentSymbol</c> capability (confirmed by inspector-log capture: only
/// <c>{"dynamicRegistration":true}</c>, no hierarchical flag) — per the LSP spec, a server MUST NOT
/// send nested <see cref="DocumentSymbol"/> to such a client and must fall back to flat
/// <see cref="SymbolInformation"/>. Prior to this fix the handler always sent nested
/// <see cref="DocumentSymbol"/> regardless of client capability, which is the most likely reason
/// VS's built-in Document Outline window / Breadcrumb Bar stayed empty despite VS successfully
/// calling <c>documentSymbol</c> and getting a 200 response: VS's typed client presumably fails to
/// deserialize a response shaped as <see cref="DocumentSymbol"/> (range/selectionRange/children)
/// against the flat <see cref="SymbolInformation"/> (location/containerName) shape it declared
/// support for. VS Code/Rider are unaffected — they do declare hierarchical support.
/// </remarks>
public sealed class DocumentSymbolHandler : IDocumentSymbolHandler
{
    private readonly IDocumentBufferService        _documentBufferService;
    private readonly IGherkinDocumentSymbolService _symbolService;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;

    private static readonly TextDocumentSelector FeatureSelector = new(
        new TextDocumentFilter { Pattern = "**/*.feature" });

    // Set from the client's declared capability in GetRegistrationOptions, which the OmniSharp
    // framework always calls before any Handle call during real capability negotiation. Defaults
    // to true (the pre-existing behavior) so a Handle call that skips registration (e.g. a unit
    // test constructing the handler directly) still gets the hierarchical shape by default.
    private bool _hierarchicalSupport = true;

    /// <summary>Initializes a new instance of the <see cref="DocumentSymbolHandler"/> class.</summary>
    public DocumentSymbolHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentSymbolService symbolService,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _symbolService         = symbolService;
        _logger                = logger;
        _recorder              = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Builds the LSP registration options for document-symbol support, recording whether the client supports hierarchical symbols.</summary>
    public DocumentSymbolRegistrationOptions GetRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        _hierarchicalSupport = capability?.HierarchicalDocumentSymbolSupport ?? true;
        return new() { DocumentSelector = FeatureSelector };
    }

    /// <summary>Handles a <c>textDocument/documentSymbol</c> request for the feature-outline tree.</summary>
    public Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, CancellationToken ct)
    {
        // Benchmarked as load-only in the synthetic harness (no published target); now also
        // has field visibility so a real P95 bar can be set.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentDocumentSymbol, request.TextDocument.Uri);

        _logger.LogInfo(
            $"Document Outline textDocument/documentSymbol: {request.TextDocument.Uri} " +
            $"(hierarchicalSupport={_hierarchicalSupport})");

        var symbols = GetSymbols(request.TextDocument.Uri);
        if (symbols.Count == 0)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(new SymbolInformationOrDocumentSymbolContainer());

        var entries = _hierarchicalSupport
            ? symbols.Select(s => SymbolInformationOrDocumentSymbol.Create(ToDocumentSymbol(s)))
            : Flatten(symbols, request.TextDocument.Uri, containerName: null)
                .Select(SymbolInformationOrDocumentSymbol.Create);

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(entries));
    }

    /// <summary>
    /// Handles the custom <c>reqnroll/documentSymbolHierarchical</c> request: always returns the
    /// nested <see cref="DocumentSymbol"/> shape, regardless of what the real LSP client (VS) has
    /// declared support for via <c>GetRegistrationOptions</c>.
    /// </summary>
    /// <remarks>
    /// The VS extension's own Navigation Bar (Issue #5 / Navigation Bar symbol source design,
    /// Option B) fetches symbols through this
    /// method rather than <c>textDocument/documentSymbol</c>. It parses the response itself
    /// (<c>GherkinNavigationBarSymbolService</c>), so it isn't affected by VS's declared client
    /// capability the way <see cref="Handle"/> is — but <see cref="Handle"/>'s capability-driven
    /// shape is per-handler-instance, not per-request, so without a separate method the Nav Bar's
    /// own fetch would silently receive the flattened shape too (and its range/children parsing,
    /// written for the nested shape, would break) whenever VS's client declares no hierarchical
    /// support.
    /// </remarks>
    public Task<IReadOnlyList<DocumentSymbol>> HandleHierarchicalAsync(
        DocumentSymbolParams request, CancellationToken ct)
    {
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollDocumentSymbolHierarchical, request.TextDocument.Uri);
        var symbols = GetSymbols(request.TextDocument.Uri);
        return Task.FromResult<IReadOnlyList<DocumentSymbol>>(symbols.Select(ToDocumentSymbol).ToList());
    }

    private IReadOnlyList<GherkinDocumentSymbol> GetSymbols(DocumentUri uri)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer?.Tags is null)
            return Array.Empty<GherkinDocumentSymbol>();

        return _symbolService.BuildSymbols(buffer.Tags);
    }

    // ── Conversion helpers ────────────────────────────────────────────────────

    private static DocumentSymbol ToDocumentSymbol(GherkinDocumentSymbol s)
    {
        var children = s.Children.Count > 0
            ? new Container<DocumentSymbol>(s.Children.Select(ToDocumentSymbol))
            : null;

        return new DocumentSymbol
        {
            Name           = s.Name,
            Detail         = s.Detail,
            Kind           = ToSymbolKind(s.Kind),
            Range          = s.Range.ToLspRange(),
            SelectionRange = s.SelectionRange.ToLspRange(),
            Children       = children,
        };
    }

    private static IEnumerable<SymbolInformation> Flatten(
        IReadOnlyList<GherkinDocumentSymbol> symbols, DocumentUri uri, string? containerName)
    {
        foreach (var s in symbols)
        {
            yield return new SymbolInformation
            {
                Name          = s.Name,
                Kind          = ToSymbolKind(s.Kind),
                ContainerName = containerName,
                Location      = new Location { Uri = uri, Range = s.Range.ToLspRange() },
            };

            foreach (var child in Flatten(s.Children, uri, s.Name))
                yield return child;
        }
    }

    private static SymbolKind ToSymbolKind(GherkinSymbolKind kind) => kind switch
    {
        GherkinSymbolKind.Feature        => SymbolKind.Module,
        GherkinSymbolKind.Background     => SymbolKind.Constructor,
        GherkinSymbolKind.Rule           => SymbolKind.Namespace,
        GherkinSymbolKind.Scenario       => SymbolKind.Method,
        GherkinSymbolKind.ScenarioOutline => SymbolKind.Method,
        GherkinSymbolKind.Step           => SymbolKind.Field,
        GherkinSymbolKind.Examples       => SymbolKind.Array,
        _                                => SymbolKind.Object,
    };
}
