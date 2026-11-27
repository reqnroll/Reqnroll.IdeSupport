using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <see cref="CommandName"/>, the command of the
/// "Copy step definition(s) to clipboard" quick fix. That code action carries no edit, only this
/// command, so running it is the user's pick: the handler sends
/// <see cref="TelemetryEvents.CodeActionCopyStepDefinitionsExecuted"/> and relays the text to the
/// client in a <see cref="CustomLspMethodNames.ReqnrollCopyToClipboard"/> notification, which each
/// client turns into a clipboard write. Arguments: <c>[text]</c>.
/// </summary>
/// <remarks>
/// LSP has no clipboard API and the server cannot write the clipboard itself (it may run remotely,
/// or under Rider's host), so the copy has to happen in the client. Visual Studio and Rider forward a
/// code action's command to the server rather than running it locally, which is why the server is
/// the one place all three clients reach and the text comes back to them as a notification.
/// </remarks>
public sealed class CopyStepDefinitionsHandler : IExecuteCommandHandler
{
    /// <summary>The command name carried by every "Copy step definition(s) to clipboard" code action.</summary>
    public const string CommandName = "reqnroll.copyStepDefinitions";

    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger     _logger;
    private readonly ILspTelemetryService? _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="CopyStepDefinitionsHandler"/> class.</summary>
    public CopyStepDefinitionsHandler(
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService = null)
    {
        _languageServer   = languageServer;
        _logger           = logger;
        _telemetryService = telemetryService;
    }

    /// <summary>Advertises <see cref="CommandName"/> as an executable <c>workspace/executeCommand</c> command.</summary>
    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new() { Commands = new Container<string>(CommandName) };

    /// <summary>Records the pick and asks the client to put the step definition text on its clipboard.</summary>
    public Task<Unit> Handle(ExecuteCommandParams request, CancellationToken cancellationToken)
    {
        if (request.Command != CommandName)
            return Task.FromResult(Unit.Value);

        _telemetryService?.SendEvent(TelemetryEvents.CodeActionCopyStepDefinitionsExecuted, new());

        // The argument comes back from the client, so it is untrusted: only a non-empty string is
        // relayed, and anything else (missing, an object, a number) is dropped. The text only ever
        // goes to the clipboard, so nothing else is checked.
        if (request.Arguments is not [JValue { Type: JTokenType.String } textArgument, ..]
            || textArgument.Value<string>() is not { Length: > 0 } text)
        {
            _logger.LogVerbose("CopyStepDefinitionsHandler: no text argument, nothing copied");
            return Task.FromResult(Unit.Value);
        }

        try
        {
            _languageServer.SendNotification(
                CustomLspMethodNames.ReqnrollCopyToClipboard,
                new JObject { ["text"] = text });
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"{CustomLspMethodNames.ReqnrollCopyToClipboard} failed: {ex.Message}");
        }

        return Task.FromResult(Unit.Value);
    }
}
