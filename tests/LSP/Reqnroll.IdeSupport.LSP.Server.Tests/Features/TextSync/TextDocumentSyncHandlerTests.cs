using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TextSync;

public class TextDocumentSyncHandlerTests
{
    private readonly IDocumentBufferService _bufferService = new DocumentBufferService();
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly IBindingMatchService _bindingMatchService = Substitute.For<IBindingMatchService>();
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly ICSharpFileTextCache _csharpFileTextCache = new CSharpFileTextCache();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    // Real implementation, not a mock: Handle now schedules its parse/discovery work through this
    // instead of awaiting it inline (issue #471), so tests need the scheduled work to actually run.
    private readonly IFeatureParseCoordinator _parseCoordinator = new FeatureParseCoordinator(Substitute.For<IIdeSupportLogger>());

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private TextDocumentSyncHandler CreateSut() =>
        new(_bufferService, _taggerService, _bindingMatchService, _csharpDiscoveryService,
            _csharpFileTextCache, _mediator, _languageServer, _logger, _parseCoordinator);

    /// <summary>Awaits the coordinator's pending entry for <paramref name="uri"/> so a test can observe the effects of Handle's scheduled (not awaited) parse/discovery work.</summary>
    private Task WaitForScheduledWorkAsync(DocumentUri uri) =>
        _parseCoordinator.WaitForReadyAsync(uri, CancellationToken.None);

    [Fact]
    public async Task Handle_DidOpen_stores_document_and_publishes_match_cache_changed_notification()
    {
        var tags = Array.Empty<IdeSupportTag>();
        _taggerService.ParseAsync(FeatureUri, 3).Returns(tags);

        var sut = CreateSut();
        var request = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = FeatureUri,
                Version = 3,
                Text = "Feature: Opened\nScenario: S\n  Given step\n",
                LanguageId = "Gherkin"
            }
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);

        _bufferService.TryGet(FeatureUri, out var buffer).Should().BeTrue();
        buffer.Should().NotBeNull();
        buffer!.Text.Should().Be(request.TextDocument.Text);
        buffer.Version.Should().Be(3);
        // Tag storage and match-set computation are delegated to IGherkinDocumentTaggerService.ParseAsync
        // (mocked here); the handler only publishes the match-cache-changed notification afterwards.

        // Handle schedules the parse/publish through IFeatureParseCoordinator instead of awaiting
        // it inline (issue #471) — wait for that scheduled work before asserting its effects.
        await WaitForScheduledWorkAsync(FeatureUri);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DidChange_updates_document_text_and_version_then_publishes()
    {
        _bufferService.Update(FeatureUri, 1, "Feature: Old\n");

        var tags = Array.Empty<IdeSupportTag>();
        _taggerService.ParseAsync(FeatureUri, 2).Returns(tags);

        var sut = CreateSut();
        var request = new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier
            {
                Uri = FeatureUri,
                Version = 2
            },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = "Feature: New\nScenario: S\n  Given changed\n" })
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);

        _bufferService.TryGet(FeatureUri, out var buffer).Should().BeTrue();
        buffer.Should().NotBeNull();
        buffer!.Text.Should().Be("Feature: New\nScenario: S\n  Given changed\n");
        buffer.Version.Should().Be(2);
        // Tag storage and match-set computation are delegated to IGherkinDocumentTaggerService.ParseAsync
        // (mocked here); the handler only publishes the match-cache-changed notification afterwards.

        await WaitForScheduledWorkAsync(FeatureUri);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DidChange_with_empty_changes_uses_empty_string_and_publishes()
    {
        var tags = Array.Empty<IdeSupportTag>();
        _taggerService.ParseAsync(FeatureUri, 4).Returns(tags);

        var sut = CreateSut();
        var request = new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier
            {
                Uri = FeatureUri,
                Version = 4
            },
            ContentChanges = new Container<TextDocumentContentChangeEvent>()
        };

        await sut.Handle(request, CancellationToken.None);

        _bufferService.TryGet(FeatureUri, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be(string.Empty);
        buffer.Version.Should().Be(4);

        await WaitForScheduledWorkAsync(FeatureUri);
        await _mediator.Received(1).Publish(
            Arg.Is<MatchCacheChangedNotification>(n => n.Uri == FeatureUri && n.Version == 4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DidClose_removes_document_buffer_and_invalidates_match_cache()
    {
        _bufferService.Update(FeatureUri, 1, "Feature: X\n");

        var sut = CreateSut();
        var request = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);
        _bufferService.TryGet(FeatureUri, out _).Should().BeFalse();
        _bindingMatchService.Received(1).InvalidateAllForDocument(FeatureUri.ToString());

        // No parse/match notification — the close path does not publish MatchCacheChangedNotification.
        await _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
    }

    [Fact]
    public async Task Handle_DidClose_rescans_feature_file_from_disk_to_keep_match_cache_complete()
    {
        _bufferService.Update(FeatureUri, 1, "Feature: X\n");

        var sut = CreateSut();
        var request = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };

        await sut.Handle(request, CancellationToken.None);

        // Closing must repopulate the match cache from disk so Find Usages / Rename still see
        // the (now closed) feature file.
        await _taggerService.Received(1).RescanClosedFileAsync(FeatureUri);
    }

    [Fact]
    public async Task Handle_DidClose_pushes_empty_diagnostics_to_clear_IDE_squiggles()
    {
        _bufferService.Update(FeatureUri, 1, "Feature: X\n");

        var sut = CreateSut();
        var request = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };

        await sut.Handle(request, CancellationToken.None);

        _languageServer.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p =>
                p.Uri == FeatureUri &&
                !p.Diagnostics.Any()));
    }

    [Fact]
    public async Task Handle_DidOpen_for_cs_file_routes_to_roslyn_discovery_and_skips_gherkin_buffer()
    {
        const string source = "namespace S { [Reqnroll.Binding] class C { } }";
        var sut = CreateSut();
        var request = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = CsUri,
                Version = 1,
                Text = source,
                LanguageId = "csharp"
            }
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);
        // .cs files are not parsed into the Gherkin document buffer.
        _bufferService.TryGet(CsUri, out _).Should().BeFalse();
        // But the live-text cache is populated regardless — see ICSharpFileTextCache.
        _csharpFileTextCache.TryGet(CsUri, out var cachedText).Should().BeTrue();
        cachedText.Should().Be(source);
        await WaitForScheduledWorkAsync(CsUri);
        await _csharpDiscoveryService.Received(1)
            .UpdateFromSourceAsync(CsUri, source, true, Arg.Any<CancellationToken>());
        await _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
    }

    [Fact]
    public async Task Handle_DidChange_for_cs_file_routes_full_text_to_roslyn_discovery()
    {
        const string source = "namespace S { [Reqnroll.Binding] class C { [Reqnroll.Given(\"x\")] void M(){} } }";
        var sut = CreateSut();
        var request = new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = CsUri, Version = 2 },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = source })
        };

        await sut.Handle(request, CancellationToken.None);

        await WaitForScheduledWorkAsync(CsUri);
        await _csharpDiscoveryService.Received(1)
            .UpdateFromSourceAsync(CsUri, source, false, Arg.Any<CancellationToken>());
        _bufferService.TryGet(CsUri, out _).Should().BeFalse();
        _csharpFileTextCache.TryGet(CsUri, out var cachedText).Should().BeTrue();
        cachedText.Should().Be(source);
    }

    [Fact]
    public async Task Handle_DidClose_for_cs_file_clears_the_live_text_cache_but_retains_bindings()
    {
        const string source = "namespace S { [Reqnroll.Binding] class C { } }";
        _csharpFileTextCache.Update(CsUri, source);

        var sut = CreateSut();
        var request = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = CsUri }
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);
        // Once closed, disk is the source of truth again for the live-text cache — VS prompts to
        // save or discard before a close reaches the server, so a stale in-memory copy could
        // otherwise be preferred over disk indefinitely.
        _csharpFileTextCache.TryGet(CsUri, out _).Should().BeFalse();
        // Closing a .cs file must not invalidate feature match state; bindings are retained until rebuild.
        _bindingMatchService.DidNotReceiveWithAnyArgs().InvalidateAllForDocument(default!);
        await _taggerService.DidNotReceiveWithAnyArgs().RescanClosedFileAsync(default!);
        // Issue #514: the server clears any binding-validation diagnostics it pushed for the
        // file, the same clear-on-close convention used for .feature files.
        _languageServer.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p => p.Uri == CsUri && !p.Diagnostics.Any()));
    }

    [Fact]
    public async Task Handle_DidSave_does_not_modify_buffer_or_publish_notification()
    {
        _bufferService.Update(FeatureUri, 7, "Feature: BeforeSave\n");

        var sut = CreateSut();
        var request = new DidSaveTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };

        var result = await sut.Handle(request, CancellationToken.None);

        result.Should().Be(Unit.Value);
        _bufferService.TryGet(FeatureUri, out var buffer).Should().BeTrue();
        buffer!.Version.Should().Be(7);
        buffer.Text.Should().Be("Feature: BeforeSave\n");

        await _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
    }
}
