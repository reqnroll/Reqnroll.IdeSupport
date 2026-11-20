using System.Collections.Immutable;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class SemanticTokensPushHandlerTests
{
    private readonly ILanguageServerFacade _facade = Substitute.For<ILanguageServerFacade>();
    private readonly ISemanticTokensService _tokenService = Substitute.For<ISemanticTokensService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly List<PublishSemanticTokensParams> _sent = new();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    // One baseline store shared by every handler a test creates, as the DI container does for the
    // transient handler instances MediatR resolves per notification.
    private readonly IPushedSemanticTokens _pushed = new PushedSemanticTokens();

    public SemanticTokensPushHandlerTests()
    {
        _facade.SendNotification("reqnroll/semanticTokens", Arg.Do<PublishSemanticTokensParams>(p => _sent.Add(p)));
    }

    private SemanticTokensPushHandler CreateSut(string? ide) =>
        new(_facade, _tokenService, new ClientIdeContext(ide), _logger, pushedTokens: _pushed);

    private void SetupTokens(SemanticTokens? tokens, int version = 1) =>
        _tokenService.GetSemanticTokensAsync(FeatureUri, version, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(tokens));

    private static SemanticTokens Tokens(params int[] data) => new() { Data = data.ToImmutableArray() };

    [Fact]
    public async Task Pushes_reqnroll_semanticTokens_notification_for_visual_studio()
    {
        SetupTokens(new SemanticTokens { Data = ImmutableArray.Create(0, 0, 8, 0, 0) });
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.Received(1).SendNotification("reqnroll/semanticTokens", Arg.Any<PublishSemanticTokensParams>());
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData(null)]
    public async Task Does_nothing_for_non_visual_studio_clients(string? ide)
    {
        var sut = CreateSut(ide);

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        await _tokenService.DidNotReceive().GetSemanticTokensAsync(
            Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }

    [Fact]
    public async Task Does_not_push_when_no_tokens_are_available()
    {
        SetupTokens(null);
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }

    [Fact]
    public async Task The_first_push_carries_the_full_set_and_no_line_range()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0));
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        var push = _sent.Should().ContainSingle().Subject;
        push.StartLine.Should().BeNull();
        push.EndLine.Should().BeNull();
        push.Data.Should().Equal(0, 0, 8, 0, 0, 2, 0, 5, 3, 0);
        push.TokenCount.Should().Be(2);
        push.Version.Should().Be(1);
    }

    [Fact]
    public async Task A_later_push_for_the_same_document_carries_only_the_changed_lines()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 9, 3, 0), version: 2); // the token on line 2 got longer
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        _sent.Should().HaveCount(2);
        var ranged = _sent[1];
        ranged.StartLine.Should().Be(2);
        ranged.EndLine.Should().Be(2);
        ranged.Data.Should().Equal(2, 0, 9, 3, 0);
        ranged.TokenCount.Should().Be(2);
        ranged.Version.Should().Be(2);
    }

    [Fact]
    public async Task A_push_is_diffed_against_the_previous_one_even_when_a_new_handler_instance_handles_it()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 9, 3, 0), version: 2);

        await CreateSut("visualstudio").Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await CreateSut("visualstudio").Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        _sent[1].StartLine.Should().Be(2, "the baseline lives in the shared service, not in the handler");
    }

    [Fact]
    public async Task Nothing_is_pushed_when_the_tokens_have_not_changed()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0), version: 2);
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        _sent.Should().ContainSingle("the client already holds exactly these tokens");
    }

    // Large enough (12,000 lines) to get the longest throttle interval.
    private static readonly string LargeText = string.Join("\n", Enumerable.Repeat("  Given a step", 12_000));

    private SemanticTokensPushHandler CreateThrottledSut(string? ide, ManualTimeProvider time, string? text = null)
    {
        var buffers = Substitute.For<IDocumentBufferService>();
        var buffer = new DocumentBuffer(FeatureUri, 1, text ?? LargeText);
        buffers.TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>()).Returns(x => { x[1] = buffer; return true; });

        return new(_facade, _tokenService, new ClientIdeContext(ide), _logger,
            pushedTokens: _pushed, throttle: new OutputThrottle(_logger, time), buffers: buffers);
    }

    [Fact]
    public async Task For_visual_studio_a_small_document_pushes_every_change_at_once()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 6, 3, 0), version: 2);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 9, 3, 0), version: 3);
        var time = new ManualTimeProvider();
        var sut = CreateThrottledSut("visualstudio", time, text: "Feature: X\n");

        foreach (var version in new[] { 1, 2, 3 })
            await sut.Handle(new MatchCacheChangedNotification(FeatureUri, version), CancellationToken.None);

        _sent.Select(p => p.Version).Should().Equal(1, 2, 3);
        time.ActiveTimerCount.Should().Be(0);
    }

    [Fact]
    public async Task For_visual_studio_a_large_document_pushes_at_once_then_the_latest_tokens_of_a_burst_once()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 6, 3, 0), version: 2);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 9, 3, 0), version: 3);
        var time = new ManualTimeProvider();
        var sut = CreateThrottledSut("visualstudio", time);

        foreach (var version in new[] { 1, 2, 3 })
            await sut.Handle(new MatchCacheChangedNotification(FeatureUri, version), CancellationToken.None);

        _sent.Should().ContainSingle("only the first change is pushed at once").Which.Version.Should().Be(1);

        time.Advance(OutputThrottlePolicy.MaxInterval - TimeSpan.FromMilliseconds(1));
        _sent.Should().ContainSingle("a large document waits the full interval");

        time.Advance(TimeSpan.FromMilliseconds(1));

        _sent.Should().HaveCount(2);
        _sent[1].Version.Should().Be(3, "the last change of the burst is always pushed");
        _sent[1].Data.Should().Equal(new[] { 2, 0, 9, 3, 0 }, "pushed against what the client received, which skipped version 2");
    }

    [Fact]
    public async Task For_visual_studio_a_change_after_a_pause_is_pushed_at_once()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0), version: 1);
        SetupTokens(Tokens(0, 0, 9, 0, 0), version: 2);
        var time = new ManualTimeProvider();
        var sut = CreateThrottledSut("visualstudio", time);

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        _sent.Should().HaveCount(2);
        time.ActiveTimerCount.Should().Be(0);
    }

    [Fact]
    public async Task Non_visual_studio_clients_never_start_a_throttle_timer()
    {
        var time = new ManualTimeProvider();
        var sut = CreateThrottledSut("vscode", time);

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        time.ActiveTimerCount.Should().Be(0);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_makes_the_next_push_the_full_set_again()
    {
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 5, 3, 0), version: 1);
        SetupTokens(Tokens(0, 0, 8, 0, 0, 2, 0, 9, 3, 0), version: 2);
        var sut = CreateSut("visualstudio");

        var calls = 0;
        _facade.When(f => f.SendNotification("reqnroll/semanticTokens", Arg.Any<PublishSemanticTokensParams>()))
               .Do(_ => { if (calls++ == 0) throw new InvalidOperationException("pipe closed"); });

        var first = () => sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 2), CancellationToken.None);

        var last = _sent[^1];
        last.StartLine.Should().BeNull("the client never received the first push, so a delta against it would be wrong");
        last.Data.Should().Equal(0, 0, 8, 0, 0, 2, 0, 9, 3, 0);
    }
}
