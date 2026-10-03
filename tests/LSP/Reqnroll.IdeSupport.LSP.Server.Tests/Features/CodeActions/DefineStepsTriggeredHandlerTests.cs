using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

public class DefineStepsTriggeredHandlerTests
{
    private const string TargetUri = "file:///workspace/Steps.cs";

    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IWindowLanguageServer _window         = Substitute.For<IWindowLanguageServer>();
    private readonly ILspTelemetryService  _telemetry      = Substitute.For<ILspTelemetryService>();

    public DefineStepsTriggeredHandlerTests()
    {
        _languageServer.Window.Returns(_window);
        SetShowDocumentSupport(true);
    }

    private void SetShowDocumentSupport(bool supported) =>
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            Capabilities = new ClientCapabilities
            {
                Window = new WindowClientCapabilities
                {
                    ShowDocument = new ShowDocumentClientCapabilities { Support = supported }
                }
            }
        });

    private DefineStepsTriggeredHandler CreateSut(string ide) =>
        new(_languageServer, new ClientIdeContext(ide), Substitute.For<IIdeSupportLogger>(), _telemetry);

    private static ExecuteCommandParams Params(string command = DefineStepsTriggeredHandler.CommandName) =>
        new() { Command = command, Arguments = new JArray(TargetUri) };

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("rider")]
    [InlineData("vscode")]
    public async Task Sends_executed_event_without_properties_for_every_client_Async(string ide)
    {
        await CreateSut(ide).Handle(Params(), CancellationToken.None);

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.DefineStepsCommandExecuted,
            Arg.Is<Dictionary<string, object?>>(p => p.Count == 0));
    }

    [Fact]
    public async Task Ignores_other_commands_Async()
    {
        await CreateSut("vscode").Handle(Params("vscode.open"), CancellationToken.None);

        _telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
        _ = _window.DidNotReceiveWithAnyArgs().SendRequest(Arg.Any<ShowDocumentParams>(), default);
    }

    [Fact]
    public async Task Reveals_the_edited_file_in_VS_Code_Async()
    {
        await CreateSut("vscode").Handle(Params(), CancellationToken.None);

        await _window.Received(1).SendRequest(
            Arg.Is<ShowDocumentParams>(p => p.Uri == DocumentUri.Parse(TargetUri) && p.TakeFocus == true),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("rider")]
    public async Task Does_not_ask_other_clients_to_show_the_file_Async(string ide)
    {
        await CreateSut(ide).Handle(Params(), CancellationToken.None);

        _ = _window.DidNotReceiveWithAnyArgs().SendRequest(Arg.Any<ShowDocumentParams>(), default);
    }

    [Fact]
    public async Task A_failing_showDocument_does_not_lose_the_event_or_throw_Async()
    {
        _window.SendRequest(Arg.Any<ShowDocumentParams>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ShowDocumentResult>(new InvalidOperationException("client refused")));

        var act = () => CreateSut("vscode").Handle(Params(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _telemetry.Received(1).SendEvent(TelemetryEvents.DefineStepsCommandExecuted, Arg.Any<Dictionary<string, object?>>());
    }

    [Fact]
    public async Task Does_not_ask_VS_Code_to_show_the_file_when_it_does_not_support_showDocument_Async()
    {
        SetShowDocumentSupport(false);

        await CreateSut("vscode").Handle(Params(), CancellationToken.None);

        _ = _window.DidNotReceiveWithAnyArgs().SendRequest(Arg.Any<ShowDocumentParams>(), default);
        _telemetry.Received(1).SendEvent(TelemetryEvents.DefineStepsCommandExecuted, Arg.Any<Dictionary<string, object?>>());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("object")]
    [InlineData("number")]
    [InlineData("empty")]
    public async Task Malformed_arguments_skip_the_reveal_but_still_send_the_event_Async(string kind)
    {
        var arguments = kind switch
        {
            "none"   => null,
            "object" => new JArray(new JObject { ["uri"] = TargetUri }),
            "number" => new JArray(42),
            _        => new JArray(""),
        };

        var act = () => CreateSut("vscode").Handle(
            new ExecuteCommandParams { Command = DefineStepsTriggeredHandler.CommandName, Arguments = arguments },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        _ = _window.DidNotReceiveWithAnyArgs().SendRequest(Arg.Any<ShowDocumentParams>(), default);
        _telemetry.Received(1).SendEvent(TelemetryEvents.DefineStepsCommandExecuted, Arg.Any<Dictionary<string, object?>>());
    }
}
