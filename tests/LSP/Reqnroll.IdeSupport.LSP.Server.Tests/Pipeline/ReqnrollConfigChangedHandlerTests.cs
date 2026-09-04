using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Parsing;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Covers <see cref="ReqnrollConfigChangedHandler"/> — previously untested (issue #576). The
/// handler schedules its reparses through <see cref="IParseCoordinator"/> instead of awaiting
/// them inline, matching every other open-document reparse path, so these tests use a real
/// <see cref="ParseCoordinator"/> (not a substitute) and wait on it explicitly, the same
/// convention as <c>TextDocumentSyncHandlerTests</c>. The actual parse-then-publish pair is
/// delegated to <see cref="IFeatureDocumentReparser"/> (issue #578) and substituted here;
/// <see cref="FeatureDocumentReparserTests"/> covers that contract directly.
/// </summary>
public class ReqnrollConfigChangedHandlerTests
{
    private readonly IDocumentBufferService _bufferService = new DocumentBufferService();
    private readonly IFeatureDocumentReparser _reparser = Substitute.For<IFeatureDocumentReparser>();
    private readonly IParseCoordinator _parseCoordinator = new ParseCoordinator(Substitute.For<IIdeSupportLogger>());
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri InScopeUri = DocumentUri.FromFileSystemPath("/workspace/proj/a.feature");
    private static readonly DocumentUri OutOfScopeUri = DocumentUri.FromFileSystemPath("/workspace/other/b.feature");

    private ReqnrollConfigChangedHandler CreateSut() =>
        new(_bufferService, _reparser, _parseCoordinator, _logger);

    private Task WaitForScheduledWorkAsync(DocumentUri uri) =>
        _parseCoordinator.WaitForReadyAsync(uri, CancellationToken.None);

    [Fact]
    public async Task Handle_schedules_a_reparse_for_every_open_buffer_under_the_workspace_root()
    {
        _bufferService.Update(InScopeUri, version: 3, "Feature: F\n");
        _reparser.ReparseOpenDocumentAsync(InScopeUri, 3, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await WaitForScheduledWorkAsync(InScopeUri);
        await _reparser.Received(1).ReparseOpenDocumentAsync(InScopeUri, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_does_not_reparse_buffers_outside_the_affected_workspace_root()
    {
        _bufferService.Update(OutOfScopeUri, version: 1, "Feature: F\n");

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await _reparser.DidNotReceive().ReparseOpenDocumentAsync(
            OutOfScopeUri, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_is_a_no_op_when_no_open_buffer_is_under_the_workspace_root()
    {
        var act = async () => await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/empty"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _reparser.DidNotReceive().ReparseOpenDocumentAsync(
            Arg.Any<DocumentUri>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_reparses_every_affected_buffer_when_several_are_open()
    {
        var secondUri = DocumentUri.FromFileSystemPath("/workspace/proj/b.feature");
        _bufferService.Update(InScopeUri, version: 1, "Feature: A\n");
        _bufferService.Update(secondUri, version: 2, "Feature: B\n");
        _reparser.ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _reparser.ReparseOpenDocumentAsync(secondUri, 2, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        await WaitForScheduledWorkAsync(InScopeUri);
        await WaitForScheduledWorkAsync(secondUri);
        await _reparser.Received(1).ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>());
        await _reparser.Received(1).ReparseOpenDocumentAsync(secondUri, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_registers_a_pending_entry_the_coordinator_can_be_awaited_on()
    {
        // The point of routing through IParseCoordinator (issue #576): a config save landing
        // while a didChange reparse is in flight for the same URI must not run two concurrent
        // reparses against it (the #554 shape). WaitForReadyAsync must observe the scheduled
        // work as pending, not return immediately.
        var reparseStarted = new TaskCompletionSource();
        var releaseReparse = new TaskCompletionSource();
        _bufferService.Update(InScopeUri, version: 1, "Feature: F\n");
        _reparser.ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            reparseStarted.TrySetResult();
            await releaseReparse.Task;
        });

        await CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);
        await reparseStarted.Task;

        var waitTask = WaitForScheduledWorkAsync(InScopeUri);
        waitTask.IsCompleted.Should().BeFalse();

        releaseReparse.SetResult();
        await waitTask;
    }

    [Fact]
    public async Task Handle_returns_without_waiting_for_the_scheduled_reparses_to_complete()
    {
        // Handle schedules and returns; it must not block the Serial dispatch lane on the
        // actual parse duration (mirrors TextDocumentSyncHandler / BindingRegistryChangedHandler).
        var releaseReparse = new TaskCompletionSource();
        _bufferService.Update(InScopeUri, version: 1, "Feature: F\n");
        _reparser.ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>())
            .Returns(_ => releaseReparse.Task);

        var handleTask = CreateSut().Handle(
            new ReqnrollConfigChangedNotification("/workspace/proj"), CancellationToken.None);

        (await Task.WhenAny(handleTask, Task.Delay(TimeSpan.FromSeconds(2)))).Should().Be(handleTask);

        releaseReparse.SetResult();
        await WaitForScheduledWorkAsync(InScopeUri);
    }

    [Fact]
    public async Task Handle_is_idempotent_when_the_same_notification_is_published_twice()
    {
        // Issue #578: ReqnrollConfigChangedNotification consumers must be idempotent -- a
        // duplicate publish (e.g. a debounced watcher firing twice for one save) must not
        // compound into extra or different work versus a single publish.
        _bufferService.Update(InScopeUri, version: 1, "Feature: F\n");
        _reparser.ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var sut = CreateSut();
        var notification = new ReqnrollConfigChangedNotification("/workspace/proj");

        await sut.Handle(notification, CancellationToken.None);
        await WaitForScheduledWorkAsync(InScopeUri);
        await sut.Handle(notification, CancellationToken.None);
        await WaitForScheduledWorkAsync(InScopeUri);

        // Exactly the same reparse, run twice with identical arguments -- not doubled, not
        // corrupted, not throwing on the second call.
        await _reparser.Received(2).ReparseOpenDocumentAsync(InScopeUri, 1, Arg.Any<CancellationToken>());
    }
}
