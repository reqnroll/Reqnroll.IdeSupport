using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Completions;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Completions;

public class CompletionAcceptedHandlerTests
{
    private readonly IFeatureUsageCounters _counters = Substitute.For<IFeatureUsageCounters>();

    [Fact]
    public async Task Counts_an_accepted_step_completion_Async()
    {
        await new CompletionAcceptedHandler(_counters).Handle(
            new ExecuteCommandParams { Command = CompletionAcceptedHandler.CommandName },
            CancellationToken.None);

        _counters.Received(1).Increment(FeatureUsageCatalog.StepCompletionAcceptedKey);
    }

    [Fact]
    public async Task Ignores_other_commands_Async()
    {
        await new CompletionAcceptedHandler(_counters).Handle(
            new ExecuteCommandParams { Command = "vscode.open" },
            CancellationToken.None);

        _counters.DidNotReceiveWithAnyArgs().Increment(default!);
    }

    [Fact]
    public async Task Tolerates_a_missing_counter_Async()
    {
        var act = () => new CompletionAcceptedHandler().Handle(
            new ExecuteCommandParams { Command = CompletionAcceptedHandler.CommandName },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Advertises_the_command()
    {
        var options = new CompletionAcceptedHandler(_counters).GetRegistrationOptions(null!, null!);

        options.Commands.Should().ContainSingle().Which.Should().Be(CompletionAcceptedHandler.CommandName);
    }

    [Fact]
    public void The_accepted_key_is_a_lookup_counter_in_the_catalogue()
        => FeatureUsageCatalog.KindOf(FeatureUsageCatalog.StepCompletionAcceptedKey).Should().Be(FeatureUsageKind.Lookup);
}
