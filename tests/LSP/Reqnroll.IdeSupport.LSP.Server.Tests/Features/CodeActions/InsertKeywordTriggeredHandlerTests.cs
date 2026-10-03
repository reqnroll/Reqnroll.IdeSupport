using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

public class InsertKeywordTriggeredHandlerTests
{
    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();

    private InsertKeywordTriggeredHandler CreateSut() => new(_telemetry);

    [Fact]
    public async Task Sends_executed_event_without_properties_Async()
    {
        await CreateSut().Handle(
            new ExecuteCommandParams { Command = InsertKeywordTriggeredHandler.CommandName },
            CancellationToken.None);

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.InsertKeywordCommandExecuted,
            Arg.Is<Dictionary<string, object?>>(p => p.Count == 0));
    }

    [Fact]
    public async Task Ignores_other_commands_Async()
    {
        await CreateSut().Handle(new ExecuteCommandParams { Command = "vscode.open" }, CancellationToken.None);

        _telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public void Advertises_only_its_own_command()
    {
        var options = CreateSut().GetRegistrationOptions(new(), new());

        options.Commands.Should().BeEquivalentTo(new[] { InsertKeywordTriggeredHandler.CommandName });
    }
}
