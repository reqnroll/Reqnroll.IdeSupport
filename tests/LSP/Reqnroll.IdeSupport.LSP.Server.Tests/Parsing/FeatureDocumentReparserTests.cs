using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Parsing;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Parsing;

/// <summary>
/// Covers <see cref="FeatureDocumentReparser"/> — the extraction of the
/// parse-then-publish pair shared by four handlers. <see cref="TextDocumentSyncHandlerTests"/>,
/// <see cref="BindingRegistryChangedHandlerTests"/>, <see cref="ReqnrollConfigChangedHandlerTests"/>,
/// and <c>DocumentActivatedHandlerTests</c> substitute <see cref="IFeatureDocumentReparser"/> and
/// verify only that they call it correctly; this class verifies the contract itself.
/// </summary>
public class FeatureDocumentReparserTests
{
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly IDocumentBufferService _bufferService = new DocumentBufferService();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private FeatureDocumentReparser CreateSut() => new(_taggerService, _bufferService, _mediator, _logger);

    // ── ReparseOpenDocumentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ReparseOpenDocumentAsync_parses_then_publishes_with_the_given_version()
    {
        _taggerService.ParseAsync(FeatureUri, 7).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, 7, CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(FeatureUri, 7);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_publishes_version_zero_when_version_is_null()
    {
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, null, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_publishes_unconditionally_even_with_no_buffer()
    {
        // Documented contract: callers of this method already know the document is open --
        // it does not check IDocumentBufferService itself. No buffer exists here at all, and
        // it must still publish, matching the four original call sites' behaviour (each of
        // which obtained the URI/version by enumerating open buffers in the first place).
        _taggerService.ParseAsync(FeatureUri, 1).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, 1, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_does_not_publish_for_a_version_the_buffer_has_moved_on_from()
    {
        // The buffer is at v8: a newer edit arrived before this reparse for v7 ran. Its own reparse publishes for v8.
        _bufferService.Update(FeatureUri, version: 8, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, 7).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, 7, CancellationToken.None);

        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_publishes_when_the_buffer_is_still_at_the_version_that_was_parsed()
    {
        _bufferService.Update(FeatureUri, version: 7, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, 7).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, 7, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_publishes_when_no_version_was_given_whatever_the_buffer_holds()
    {
        _bufferService.Update(FeatureUri, version: 8, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseOpenDocumentAsync(FeatureUri, null, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseOpenDocumentAsync_is_idempotent_when_called_twice_with_the_same_arguments()
    {
        // Consumers of MatchCacheChangedNotification must be idempotent -- calling
        // the reparse-then-publish pair twice with identical inputs must not compound into a
        // different observable outcome than calling it once (beyond the expected doubled count).
        _taggerService.ParseAsync(FeatureUri, 1).Returns(Array.Empty<IdeSupportTag>());

        var sut = CreateSut();
        await sut.ReparseOpenDocumentAsync(FeatureUri, 1, CancellationToken.None);
        await sut.ReparseOpenDocumentAsync(FeatureUri, 1, CancellationToken.None);

        await _taggerService.Received(2).ParseAsync(FeatureUri, 1);
        await _mediator.Received(2).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 1),
            Arg.Any<CancellationToken>());
    }

    // ── ReparseIfOpenAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ReparseIfOpenAsync_parses_and_publishes_the_buffers_actual_current_version()
    {
        _bufferService.Update(FeatureUri, version: 5, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseIfOpenAsync(FeatureUri, CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseIfOpenAsync_publishes_version_zero_when_the_buffer_has_no_version()
    {
        _bufferService.Update(FeatureUri, version: null, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseIfOpenAsync(FeatureUri, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseIfOpenAsync_is_a_safe_no_op_when_the_document_is_not_open()
    {
        // No buffer for this URI at all — the client-side signal that triggers this path can
        // race ahead of didOpen. Must not throw, and must not publish for a document the
        // server does not know about.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        var act = async () => await CreateSut().ReparseIfOpenAsync(FeatureUri, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparseIfOpenAsync_still_parses_even_when_the_document_is_not_open()
    {
        // The parse itself is the "force a fresh recompute" step and must always be attempted,
        // regardless of whether a buffer turns out to exist afterward.
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ReparseIfOpenAsync(FeatureUri, CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(FeatureUri, null);
    }

    [Fact]
    public async Task ReparseIfOpenAsync_is_idempotent_when_called_twice_and_the_document_stays_open()
    {
        _bufferService.Update(FeatureUri, version: 2, "Feature: F\n");
        _taggerService.ParseAsync(FeatureUri, null).Returns(Array.Empty<IdeSupportTag>());

        var sut = CreateSut();
        await sut.ReparseIfOpenAsync(FeatureUri, CancellationToken.None);
        await sut.ReparseIfOpenAsync(FeatureUri, CancellationToken.None);

        await _mediator.Received(2).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 2),
            Arg.Any<CancellationToken>());
    }

    // ── RescanClosedDocumentAsync ────────────────────────────────────────────

    [Fact]
    public async Task RescanClosedDocumentAsync_rescans_from_disk_then_publishes_the_closed_file_notification()
    {
        await CreateSut().RescanClosedDocumentAsync(FeatureUri, CancellationToken.None);

        Received.InOrder(() =>
        {
            _ = _taggerService.RescanClosedFileAsync(FeatureUri);
            _ = _mediator.Publish(
                Arg.Is<ClosedFileMatchCacheChangedNotification>(n => n.Uri == FeatureUri),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RescanClosedDocumentAsync_publishes_even_when_the_rescan_found_nothing_to_store()
    {
        // The close already dropped the file's cache entries; a missing file only means nothing replaces them.
        _taggerService.RescanClosedFileAsync(FeatureUri).Returns(Task.CompletedTask);

        await CreateSut().RescanClosedDocumentAsync(FeatureUri, CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Any<ClosedFileMatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RescanClosedDocumentAsync_does_not_publish_the_open_document_notification()
    {
        await CreateSut().RescanClosedDocumentAsync(FeatureUri, CancellationToken.None);

        await _mediator.DidNotReceive().Publish(
            Arg.Any<MatchCacheChangedNotification>(), Arg.Any<CancellationToken>());
    }
}
