using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.DocumentActivated;

public class DocumentActivatedHandlerTests
{
    private readonly IDocumentBufferService _bufferService = new DocumentBufferService();
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private DocumentActivatedHandler CreateSut() =>
        new(_taggerService, _bufferService, _mediator, _logger);

    [Fact]
    public async Task HandleAsync_reparses_and_publishes_match_cache_changed_for_an_open_document()
    {
        _bufferService.Update(FeatureUri, version: 5, "Feature: F\nScenario: S\n  Given step\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<DeveroomTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_publishes_version_zero_when_the_buffer_has_no_version()
    {
        _bufferService.Update(FeatureUri, version: null, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<DeveroomTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_is_a_safe_no_op_when_the_document_is_not_open()
    {
        // No buffer for this URI — e.g. the VS-side activation signal raced ahead of didOpen.
        // Must not throw and must not republish anything for a document the server doesn't know about.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<DeveroomTag>());

        var act = async () => await CreateSut().HandleAsync(
            new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_still_calls_ParseAsync_even_when_the_document_is_not_open()
    {
        // ParseAsync itself is the "force a fresh recompute" step; it must always be attempted
        // regardless of whether a buffer turns out to exist afterward.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<DeveroomTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
    }
}
