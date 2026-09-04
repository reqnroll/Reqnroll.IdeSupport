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
    private readonly IParseCoordinator _parseCoordinator = new ParseCoordinator(Substitute.For<IIdeSupportLogger>());

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private DocumentActivatedHandler CreateSut() =>
        new(_taggerService, _bufferService, _mediator, _parseCoordinator, _logger);

    private Task WaitForScheduledWorkAsync(DocumentUri uri) =>
        _parseCoordinator.WaitForReadyAsync(uri, CancellationToken.None);

    [Fact]
    public async Task HandleAsync_reparses_and_publishes_match_cache_changed_for_an_open_document()
    {
        _bufferService.Update(FeatureUri, version: 5, "Feature: F\nScenario: S\n  Given step\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        // Handle schedules the parse/publish through IParseCoordinator instead of awaiting it
        // inline (issue #576) — wait for that scheduled work before asserting its effects.
        await WaitForScheduledWorkAsync(FeatureUri);
        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_publishes_version_zero_when_the_buffer_has_no_version()
    {
        _bufferService.Update(FeatureUri, version: null, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        await WaitForScheduledWorkAsync(FeatureUri);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_is_a_safe_no_op_when_the_document_is_not_open()
    {
        // No buffer for this URI — e.g. the VS-side activation signal raced ahead of didOpen.
        // Must not throw and must not republish anything for a document the server doesn't know about.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        var act = async () =>
        {
            await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);
            await WaitForScheduledWorkAsync(FeatureUri);
        };

        await act.Should().NotThrowAsync();
        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_still_calls_ParseAsync_even_when_the_document_is_not_open()
    {
        // ParseAsync itself is the "force a fresh recompute" step; it must always be attempted
        // regardless of whether a buffer turns out to exist afterward.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);
        await WaitForScheduledWorkAsync(FeatureUri);

        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
    }

    [Fact]
    public async Task HandleAsync_registers_a_pending_entry_the_coordinator_can_be_awaited_on()
    {
        // The whole point of routing through IParseCoordinator (issue #576): FoldingRangeHandler/
        // DocumentSymbolHandler await WaitForReadyAsync before reading buffer.Tags, so an
        // activation-triggered reparse must be visible there, not just a direct-edit one.
        var parseStarted = new TaskCompletionSource();
        var releaseParse = new TaskCompletionSource();
        _bufferService.Update(FeatureUri, version: 1, "Feature: F\n");

        async Task<IReadOnlyCollection<IdeSupportTag>> BlockUntilReleasedAsync()
        {
            parseStarted.TrySetResult();
            await releaseParse.Task;
            return Array.Empty<IdeSupportTag>();
        }
        _taggerService.ParseAsync(FeatureUri, null).Returns(_ => BlockUntilReleasedAsync());

        var handleTask = CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);
        await handleTask;
        await parseStarted.Task;

        // The scheduled work is still in flight; WaitForReadyAsync must not complete yet.
        var waitTask = WaitForScheduledWorkAsync(FeatureUri);
        waitTask.IsCompleted.Should().BeFalse();

        releaseParse.SetResult();
        await waitTask;
    }
}
