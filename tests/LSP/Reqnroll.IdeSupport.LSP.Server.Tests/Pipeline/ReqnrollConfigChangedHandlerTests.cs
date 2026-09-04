using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Covers <see cref="ReqnrollConfigChangedHandler"/> — previously untested (issue #576). The
/// handler now schedules its reparses through <see cref="IParseCoordinator"/> instead of
/// awaiting them inline, matching every other open-document reparse path, so these tests use a
/// real <see cref="ParseCoordinator"/> (not a substitute) and wait on it explicitly, the same
/// convention as <c>TextDocumentSyncHandlerTests</c>.
/// </summary>
public class ReqnrollConfigChangedHandlerTests
{
    private readonly IDocumentBufferService _bufferService = new DocumentBufferService();
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IParseCoordinator _parseCoordinator = new ParseCoordinator(Substitute.For<IIdeSupportLogger>());
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri InScopeUri = DocumentUri.FromFileSystemPath("/workspace/proj/a.feature");
    private static readonly DocumentUri OutOfScopeUri = DocumentUri.FromFileSystemPath("/workspace/other/b.feature");

    private ReqnrollConfigChangedHandler CreateSut() =>
        new(_bufferService, _taggerService, _mediator, _parseCoordinator, _logger);

    private Task WaitForScheduledWorkAsync(DocumentUri uri) =>
        _parseCoordinator.WaitForReadyAsync(uri, CancellationToken.None);

    [Fact]
    public async Task Handle_schedules_a_reparse_for_every_open_buffer_under_the_workspace_root()
    {
        _bufferService.Update(InScopeUri, version: 3, "Feature: F\n");
        _taggerService.ParseAsync(InScopeUri, 3).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await WaitForScheduledWorkAsync(InScopeUri);
        await _taggerService.Received(1).ParseAsync(InScopeUri, 3);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == InScopeUri && n.Version == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_does_not_reparse_buffers_outside_the_affected_workspace_root()
    {
        _bufferService.Update(OutOfScopeUri, version: 1, "Feature: F\n");

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await _taggerService.DidNotReceive().ParseAsync(OutOfScopeUri, Arg.Any<int?>());
        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_is_a_no_op_when_no_open_buffer_is_under_the_workspace_root()
    {
        var act = async () => await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/empty"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_reparses_every_affected_buffer_when_several_are_open()
    {
        var secondUri = DocumentUri.FromFileSystemPath("/workspace/proj/b.feature");
        _bufferService.Update(InScopeUri, version: 1, "Feature: A\n");
        _bufferService.Update(secondUri, version: 2, "Feature: B\n");
        _taggerService.ParseAsync(InScopeUri, 1).Returns(Array.Empty<IdeSupportTag>());
        _taggerService.ParseAsync(secondUri, 2).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await WaitForScheduledWorkAsync(InScopeUri);
        await WaitForScheduledWorkAsync(secondUri);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == InScopeUri), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == secondUri), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_registers_a_pending_entry_the_coordinator_can_be_awaited_on()
    {
        // The point of routing through IParseCoordinator (issue #576): a config save landing
        // while a didChange reparse is in flight for the same URI must not run two concurrent
        // ParseAsync calls against it (the #554 shape). WaitForReadyAsync must observe the
        // scheduled work as pending, not return immediately.
        var parseStarted = new TaskCompletionSource();
        var releaseParse = new TaskCompletionSource();
        _bufferService.Update(InScopeUri, version: 1, "Feature: F\n");

        async Task<IReadOnlyCollection<IdeSupportTag>> BlockUntilReleasedAsync()
        {
            parseStarted.TrySetResult();
            await releaseParse.Task;
            return Array.Empty<IdeSupportTag>();
        }
        _taggerService.ParseAsync(InScopeUri, 1).Returns(_ => BlockUntilReleasedAsync());

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);
        await parseStarted.Task;

        var waitTask = WaitForScheduledWorkAsync(InScopeUri);
        waitTask.IsCompleted.Should().BeFalse();

        releaseParse.SetResult();
        await waitTask;
    }

    [Fact]
    public async Task Handle_returns_without_waiting_for_the_scheduled_reparses_to_complete()
    {
        // Handle schedules and returns; it must not block the Serial dispatch lane on the
        // actual parse duration (mirrors TextDocumentSyncHandler / BindingRegistryChangedHandler).
        var releaseParse = new TaskCompletionSource();
        _bufferService.Update(InScopeUri, version: 1, "Feature: F\n");

        async Task<IReadOnlyCollection<IdeSupportTag>> BlockUntilReleasedAsync()
        {
            await releaseParse.Task;
            return Array.Empty<IdeSupportTag>();
        }
        _taggerService.ParseAsync(InScopeUri, 1).Returns(_ => BlockUntilReleasedAsync());

        var handleTask = CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        (await Task.WhenAny(handleTask, Task.Delay(TimeSpan.FromSeconds(2)))).Should().Be(handleTask);

        releaseParse.SetResult();
        await WaitForScheduledWorkAsync(InScopeUri);
    }
}
