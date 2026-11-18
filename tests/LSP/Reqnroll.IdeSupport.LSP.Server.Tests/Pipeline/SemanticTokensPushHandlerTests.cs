using System.Collections.Immutable;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class SemanticTokensPushHandlerTests
{
    private readonly ILanguageServerFacade _facade = Substitute.For<ILanguageServerFacade>();
    private readonly ISemanticTokensService _tokenService = Substitute.For<ISemanticTokensService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly DocumentBufferService _documents = new();
    private readonly ManualDebouncer _debouncer = new();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri OtherUri = DocumentUri.FromFileSystemPath("/workspace/other.feature");

    private static readonly SemanticTokens SomeTokens = new() { Data = ImmutableArray.Create(0, 0, 8, 0, 0) };

    private SemanticTokensPushHandler CreateSut(string? ide = "visualstudio") =>
        new(_facade, _tokenService, new ClientIdeContext(ide), _logger, _documents, _debouncer);

    private void Open(DocumentUri uri, int version) => _documents.Update(uri, version, "Feature: x");

    private void SetupTokens(SemanticTokens? tokens) =>
        _tokenService.GetSemanticTokensAsync(Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(tokens));

    private List<PublishSemanticTokensParams> Pushed() =>
        _facade.ReceivedCalls()
               .Where(c => c.GetMethodInfo().Name == nameof(ILanguageServerFacade.SendNotification))
               .Select(c => c.GetArguments()[1])
               .OfType<PublishSemanticTokensParams>()
               .ToList();

    [Fact]
    public async Task Pushes_reqnroll_semanticTokens_notification_for_visual_studio_after_the_quiet_period()
    {
        SetupTokens(SomeTokens);
        Open(FeatureUri, 1);

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());

        await _debouncer.FlushAsync();

        _facade.Received(1).SendNotification("reqnroll/semanticTokens", Arg.Any<PublishSemanticTokensParams>());
        Assert.Equal(SemanticTokensPushHandler.CoalesceDelay, _debouncer.LastDelay);
    }

    [Fact]
    public async Task A_burst_of_changes_for_one_document_collapses_to_one_push()
    {
        SetupTokens(SomeTokens);
        Open(FeatureUri, 1);

        for (var v = 1; v <= 5; v++)
            await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, v), CancellationToken.None);
        await _debouncer.FlushAsync();

        Assert.Single(Pushed());
        await _tokenService.Received(1).GetSemanticTokensAsync(
            FeatureUri, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Different_documents_are_debounced_independently()
    {
        SetupTokens(SomeTokens);
        Open(FeatureUri, 1);
        Open(OtherUri, 1);

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await CreateSut().Handle(new MatchCacheChangedNotification(OtherUri, 1), CancellationToken.None);
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await _debouncer.FlushAsync();

        var pushed = Pushed();
        Assert.Equal(2, pushed.Count);
        Assert.Contains(pushed, p => p.Uri == FeatureUri.ToString());
        Assert.Contains(pushed, p => p.Uri == OtherUri.ToString());
    }

    [Fact]
    public async Task Pushes_the_newest_version_present_when_the_quiet_period_ends()
    {
        SetupTokens(SomeTokens);
        Open(FeatureUri, 1);
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        // The buffer moves on before the quiet period ends.
        Open(FeatureUri, 7);
        await _debouncer.FlushAsync();

        var push = Assert.Single(Pushed());
        Assert.Equal(7, push.Version);
        await _tokenService.Received(1).GetSemanticTokensAsync(FeatureUri, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_push_when_the_document_is_closed_within_the_quiet_period()
    {
        SetupTokens(SomeTokens);
        Open(FeatureUri, 1);
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _documents.Remove(FeatureUri);
        await _debouncer.FlushAsync();

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
        await _tokenService.DidNotReceive().GetSemanticTokensAsync(
            Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData(null)]
    public async Task Never_schedules_for_non_visual_studio_clients(string? ide)
    {
        Open(FeatureUri, 1);

        await CreateSut(ide).Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await _debouncer.FlushAsync();

        Assert.Equal(0, _debouncer.ScheduleCount);
        await _tokenService.DidNotReceive().GetSemanticTokensAsync(
            Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }

    [Fact]
    public async Task Does_not_push_when_no_tokens_are_available()
    {
        SetupTokens(null);
        Open(FeatureUri, 1);

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);
        await _debouncer.FlushAsync();

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }

    /// <summary>Keeps only the last action scheduled per key, like the real debouncer, and runs them on demand.</summary>
    private sealed class ManualDebouncer : IRefreshDebouncer
    {
        private readonly Dictionary<string, Func<CancellationToken, Task>> _pending = new();

        public int ScheduleCount { get; private set; }
        public TimeSpan LastDelay { get; private set; }

        public void Schedule(string key, TimeSpan delay, Func<CancellationToken, Task> action)
        {
            ScheduleCount++;
            LastDelay = delay;
            _pending[key] = action;
        }

        public async Task FlushAsync()
        {
            var actions = _pending.Values.ToList();
            _pending.Clear();
            foreach (var action in actions)
                await action(CancellationToken.None);
        }
    }
}
