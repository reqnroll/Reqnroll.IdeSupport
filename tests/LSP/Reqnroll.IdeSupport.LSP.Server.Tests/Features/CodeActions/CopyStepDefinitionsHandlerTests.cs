using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

public class CopyStepDefinitionsHandlerTests
{
    private const string Snippet = "    [When(\"I press add\")]\r\n    public void WhenIPressAdd()\r\n    {\r\n    }\r\n";

    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly ILspTelemetryService  _telemetry      = Substitute.For<ILspTelemetryService>();

    private CopyStepDefinitionsHandler CreateSut() =>
        new(_languageServer, Substitute.For<IIdeSupportLogger>(), _telemetry);

    private static ExecuteCommandParams Params(JArray arguments, string command = CopyStepDefinitionsHandler.CommandName) =>
        new() { Command = command, Arguments = arguments };

    [Fact]
    public void Advertises_the_copy_command()
    {
        var options = CreateSut().GetRegistrationOptions(new(), new());

        options.Commands.Should().Equal(CopyStepDefinitionsHandler.CommandName);
    }

    [Fact]
    public async Task Relays_the_text_to_the_client_clipboard_Async()
    {
        await CreateSut().Handle(Params(new JArray(Snippet)), CancellationToken.None);

        _languageServer.Received(1).SendNotification(
            CustomLspMethodNames.ReqnrollCopyToClipboard,
            Arg.Is<object>(o => o is JObject && ((JObject)o)["text"]!.Value<string>() == Snippet));
    }

    [Fact]
    public async Task Sends_executed_event_without_properties_Async()
    {
        await CreateSut().Handle(Params(new JArray(Snippet)), CancellationToken.None);

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.CodeActionCopyStepDefinitionsExecuted,
            Arg.Is<Dictionary<string, object?>>(p => p.Count == 0));
    }

    [Fact]
    public async Task Ignores_other_commands_Async()
    {
        await CreateSut().Handle(Params(new JArray(Snippet), command: "reqnroll.other"), CancellationToken.None);

        _telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
        _languageServer.DidNotReceiveWithAnyArgs().SendNotification(default!, default(object)!);
    }

    public static TheoryData<JArray> UnusableArguments => new()
    {
        new JArray(),
        new JArray(""),
        new JArray(42),
        new JArray(new JObject { ["text"] = Snippet }),
    };

    [Theory]
    [MemberData(nameof(UnusableArguments))]
    public async Task Copies_nothing_for_an_unusable_argument_but_still_records_the_pick_Async(JArray arguments)
    {
        await CreateSut().Handle(Params(arguments), CancellationToken.None);

        _languageServer.DidNotReceiveWithAnyArgs().SendNotification(default!, default(object)!);
        _telemetry.Received(1).SendEvent(
            TelemetryEvents.CodeActionCopyStepDefinitionsExecuted, Arg.Any<Dictionary<string, object?>>());
    }

    [Fact]
    public async Task A_failed_notification_does_not_fail_the_command_Async()
    {
        _languageServer
            .When(s => s.SendNotification(Arg.Any<string>(), Arg.Any<object>()))
            .Do(_ => throw new InvalidOperationException("connection closed"));

        var act = () => CreateSut().Handle(Params(new JArray(Snippet)), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
