#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Folding;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.Folding;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Folding;

public class FoldingRangeHandlerTests
{
    private readonly IDocumentBufferService         _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IGherkinFoldingRangeService     _foldingService = Substitute.For<IGherkinFoldingRangeService>();
    private readonly IFeatureParseCoordinator       _parseCoordinator = Substitute.For<IFeatureParseCoordinator>();
    private readonly IIdeSupportLogger                 _logger        = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private FoldingRangeHandler CreateSut() =>
        new(_bufferService, _foldingService, _parseCoordinator, _logger);

    private static FoldingRangeRequestParam RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private void SetupBuffer(DocumentUri uri, IReadOnlyCollection<DeveroomTag>? tags)
    {
        var buf = new DocumentBuffer(uri, 1, "Feature: X\n", tags);
        DocumentBuffer? outBuf;
        _bufferService.TryGet(uri, out outBuf)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    // ── Guard rails ───────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_null_when_buffer_not_found_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_null_when_tags_not_yet_computed_Async()
    {
        SetupBuffer(FeatureUri, tags: null);

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_service_returns_no_ranges_Async()
    {
        SetupBuffer(FeatureUri, Array.Empty<DeveroomTag>());
        _foldingService.BuildFoldingRanges(Arg.Any<IReadOnlyCollection<DeveroomTag>>())
                       .Returns(Array.Empty<GherkinFoldingRange>());

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_folding_ranges_when_service_provides_them_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, tags);
        _foldingService.BuildFoldingRanges(tags)
            .Returns(new[]
            {
                new GherkinFoldingRange(1, 3),
                new GherkinFoldingRange(4, 5),
            });

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain(r => r.StartLine == 1 && r.EndLine == 3);
        result.Should().Contain(r => r.StartLine == 4 && r.EndLine == 5);
    }

    [Fact]
    public async Task Folding_ranges_have_no_set_kind_by_default_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, tags);
        _foldingService.BuildFoldingRanges(tags)
            .Returns(new[] { new GherkinFoldingRange(1, 2) });

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().ContainSingle();
        result!.First().Kind.Should().BeNull();
    }

    [Fact]
    public async Task Calls_folding_service_with_buffer_tags_Async()
    {
        var tags = Array.Empty<DeveroomTag>();
        SetupBuffer(FeatureUri, tags);
        _foldingService.BuildFoldingRanges(Arg.Any<IReadOnlyCollection<DeveroomTag>>())
                       .Returns(Array.Empty<GherkinFoldingRange>());

        await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        _foldingService.Received(1).BuildFoldingRanges(tags);
    }
}
