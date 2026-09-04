using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentActivated;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.DocumentActivated;

/// <summary>
/// Covers <see cref="DocumentActivatedHandler"/>, which since issue #578 delegates the actual
/// parse-then-publish work to <see cref="IFeatureDocumentReparser.ReparseIfOpenAsync"/> — these
/// tests verify the handler schedules that call correctly through <see cref="IParseCoordinator"/>;
/// <see cref="Pipeline.FeatureDocumentReparserTests"/> covers the reparse/publish contract itself.
/// </summary>
public class DocumentActivatedHandlerTests
{
    private readonly IFeatureDocumentReparser _reparser = Substitute.For<IFeatureDocumentReparser>();
    private readonly IParseCoordinator _parseCoordinator = new ParseCoordinator(Substitute.For<IIdeSupportLogger>());

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private DocumentActivatedHandler CreateSut() => new(_reparser, _parseCoordinator);

    private Task WaitForScheduledWorkAsync(DocumentUri uri) =>
        _parseCoordinator.WaitForReadyAsync(uri, CancellationToken.None);

    [Fact]
    public async Task HandleAsync_schedules_ReparseIfOpenAsync_for_the_activated_document()
    {
        _reparser.ReparseIfOpenAsync(FeatureUri, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);

        // Handle schedules the work through IParseCoordinator instead of awaiting it inline
        // (issue #576) — wait for that scheduled work before asserting its effects.
        await WaitForScheduledWorkAsync(FeatureUri);
        await _reparser.Received(1).ReparseIfOpenAsync(FeatureUri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_does_not_throw_when_the_reparser_finds_no_open_buffer()
    {
        // ReparseIfOpenAsync itself is documented as a safe no-op for a document that isn't
        // open — e.g. the VS-side activation signal raced ahead of didOpen. This handler must
        // not add its own throw/guard around that.
        _reparser.ReparseIfOpenAsync(FeatureUri, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var act = async () =>
        {
            await CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);
            await WaitForScheduledWorkAsync(FeatureUri);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_registers_a_pending_entry_the_coordinator_can_be_awaited_on()
    {
        // The whole point of routing through IParseCoordinator (issue #576): FoldingRangeHandler/
        // DocumentSymbolHandler await WaitForReadyAsync before reading buffer.Tags, so an
        // activation-triggered reparse must be visible there, not just a direct-edit one.
        var reparseStarted = new TaskCompletionSource();
        var releaseReparse = new TaskCompletionSource();
        _reparser.ReparseIfOpenAsync(FeatureUri, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            reparseStarted.TrySetResult();
            await releaseReparse.Task;
        });

        var handleTask = CreateSut().HandleAsync(new DocumentActivatedParams { Uri = FeatureUri }, CancellationToken.None);
        await handleTask;
        await reparseStarted.Task;

        // The scheduled work is still in flight; WaitForReadyAsync must not complete yet.
        var waitTask = WaitForScheduledWorkAsync(FeatureUri);
        waitTask.IsCompleted.Should().BeFalse();

        releaseReparse.SetResult();
        await waitTask;
    }
}
