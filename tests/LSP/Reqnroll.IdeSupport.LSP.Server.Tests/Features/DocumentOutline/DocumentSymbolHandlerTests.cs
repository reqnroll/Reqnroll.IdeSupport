#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentOutline;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.DocumentOutline;

public class DocumentSymbolHandlerTests
{
    private readonly IDocumentBufferService       _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly IGherkinDocumentSymbolService _symbolService  = Substitute.For<IGherkinDocumentSymbolService>();
    private readonly IFeatureParseCoordinator     _parseCoordinator = Substitute.For<IFeatureParseCoordinator>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private DocumentSymbolHandler CreateSut() =>
        new(_bufferService, _symbolService, _parseCoordinator, _logger);

    private static DocumentSymbolParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private void SetupBuffer(DocumentUri uri, string text, IReadOnlyCollection<IdeSupportTag>? tags)
    {
        var buf = new DocumentBuffer(uri, 1, text, tags);
        DocumentBuffer? outBuf;
        _bufferService.TryGet(uri, out outBuf)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    private static GherkinDocumentSymbol MakeSymbol(
        string name, GherkinSymbolKind kind, IReadOnlyList<GherkinDocumentSymbol>? children = null)
    {
        var snap = new LspTextSnapshot(FeatureUri.ToString(), 1, "Feature: X\n");
        var range = new GherkinRange(snap, 0, 10);
        return new GherkinDocumentSymbol(name, null, kind, range, range,
            children ?? Array.Empty<GherkinDocumentSymbol>());
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_null_when_buffer_not_found_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_tags_not_yet_computed_Async()
    {
        SetupBuffer(FeatureUri, "Feature: X\n", tags: null);

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_null_when_service_returns_no_symbols_Async()
    {
        SetupBuffer(FeatureUri, "Feature: X\n", tags: Array.Empty<IdeSupportTag>());
        _symbolService.BuildSymbols(Arg.Any<IReadOnlyCollection<IdeSupportTag>>())
                      .Returns(Array.Empty<GherkinDocumentSymbol>());

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_container_when_symbols_exist_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Top_level_symbol_has_correct_name_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("MyFeature", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        var first = result!.First();
        first.IsDocumentSymbol.Should().BeTrue();
        first.DocumentSymbol!.Name.Should().Be("MyFeature");
    }

    [Fact]
    public async Task Feature_symbol_maps_to_Module_kind_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Feature) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Module);
    }

    [Fact]
    public async Task Scenario_symbol_maps_to_Method_kind_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("X", GherkinSymbolKind.Scenario) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Method);
    }

    [Fact]
    public async Task Step_symbol_maps_to_Field_kind_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("Given a step", GherkinSymbolKind.Step) });

        var result = await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.First().DocumentSymbol!.Kind.Should().Be(SymbolKind.Field);
    }

    [Fact]
    public async Task Calls_symbol_service_with_buffer_tags_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(Arg.Any<IReadOnlyCollection<IdeSupportTag>>())
                      .Returns(Array.Empty<GherkinDocumentSymbol>());

        await CreateSut().Handle(RequestFor(FeatureUri), CancellationToken.None);

        _symbolService.Received(1).BuildSymbols(tags);
    }

    // ── hierarchicalDocumentSymbolSupport capability (Visual Studio compatibility) ─────────────
    //
    // Visual Studio's LSP client does not declare hierarchicalDocumentSymbolSupport, so per the
    // LSP spec the server must return flat SymbolInformation instead of nested DocumentSymbol —
    // otherwise VS's typed client fails to deserialize the response and windows like Document
    // Outline / Breadcrumb Bar stay empty despite a successful 200 response.

    [Fact]
    public async Task Hierarchical_support_false_returns_flat_SymbolInformation_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("MyFeature", GherkinSymbolKind.Feature) });

        var sut = CreateSut();
        sut.GetRegistrationOptions(
            new DocumentSymbolCapability { HierarchicalDocumentSymbolSupport = false },
            new ClientCapabilities());

        var result = await sut.Handle(RequestFor(FeatureUri), CancellationToken.None);

        var first = result!.First();
        first.IsDocumentSymbol.Should().BeFalse();
        first.SymbolInformation!.Name.Should().Be("MyFeature");
        first.SymbolInformation!.Kind.Should().Be(SymbolKind.Module);
        first.SymbolInformation!.Location.Uri.Should().Be(FeatureUri);
        first.SymbolInformation!.ContainerName.Should().BeNull();
    }

    [Fact]
    public async Task Hierarchical_support_false_flattens_children_with_parent_as_containerName_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        var scenario = MakeSymbol("Add two numbers", GherkinSymbolKind.Scenario,
            new[] { MakeSymbol("Given a step", GherkinSymbolKind.Step) });
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("MyFeature", GherkinSymbolKind.Feature, new[] { scenario }) });

        var sut = CreateSut();
        sut.GetRegistrationOptions(
            new DocumentSymbolCapability { HierarchicalDocumentSymbolSupport = false },
            new ClientCapabilities());

        var result = await sut.Handle(RequestFor(FeatureUri), CancellationToken.None);

        result!.Should().HaveCount(3);
        var entries = result!.Select(e => e.SymbolInformation!).ToList();
        entries[0].Name.Should().Be("MyFeature");
        entries[0].ContainerName.Should().BeNull();
        entries[1].Name.Should().Be("Add two numbers");
        entries[1].ContainerName.Should().Be("MyFeature");
        entries[2].Name.Should().Be("Given a step");
        entries[2].ContainerName.Should().Be("Add two numbers");
    }

    [Fact]
    public async Task Hierarchical_support_true_returns_nested_DocumentSymbol_Async()
    {
        var tags = Array.Empty<IdeSupportTag>();
        SetupBuffer(FeatureUri, "Feature: X\n", tags);
        _symbolService.BuildSymbols(tags)
                      .Returns(new[] { MakeSymbol("MyFeature", GherkinSymbolKind.Feature) });

        var sut = CreateSut();
        sut.GetRegistrationOptions(
            new DocumentSymbolCapability { HierarchicalDocumentSymbolSupport = true },
            new ClientCapabilities());

        var result = await sut.Handle(RequestFor(FeatureUri), CancellationToken.None);

        var first = result!.First();
        first.IsDocumentSymbol.Should().BeTrue();
        first.DocumentSymbol!.Name.Should().Be("MyFeature");
    }
}
