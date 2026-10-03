using MediatR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <see cref="CommandName"/>, the <see cref="CodeAction.Command"/>
/// every "Define step(s)" quick fix carries. A client runs a code action's command after applying
/// its <c>WorkspaceEdit</c>, so receiving it is the server's only exact signal that the user
/// picked a Define Steps action (issue #847) — it sends <see cref="TelemetryEvents.DefineStepsCommandExecuted"/>.
/// Arguments: <c>[targetFileUri]</c>.
/// </summary>
/// <remarks>
/// The command used to be <c>vscode.open</c>, which VS Code runs locally to reveal the edited file.
/// A code action has only one command slot, so VS Code now gets that behaviour from the server via
/// <c>window/showDocument</c> instead. (Visual Studio and Rider never handled <c>vscode.open</c> — they
/// forwarded it to the server and got "Method not found" — so for them this is a pure gain.)
/// </remarks>
public sealed class DefineStepsTriggeredHandler : IExecuteCommandHandler
{
    /// <summary>The command name carried by every Define Steps code action.</summary>
    public const string CommandName = "reqnroll.defineStepsTriggered";

    private readonly ILanguageServerFacade _languageServer;
    private readonly ClientIdeContext      _clientIde;
    private readonly IIdeSupportLogger     _logger;
    private readonly ILspTelemetryService? _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="DefineStepsTriggeredHandler"/> class.</summary>
    public DefineStepsTriggeredHandler(
        ILanguageServerFacade languageServer,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService = null)
    {
        _languageServer   = languageServer;
        _clientIde        = clientIde;
        _logger           = logger;
        _telemetryService = telemetryService;
    }

    /// <summary>Advertises <see cref="CommandName"/> as an executable <c>workspace/executeCommand</c> command.</summary>
    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new() { Commands = new Container<string>(CommandName) };

    /// <summary>Records the trigger and, for VS Code, reveals the edited file.</summary>
    public async Task<Unit> Handle(ExecuteCommandParams request, CancellationToken cancellationToken)
    {
        if (request.Command != CommandName)
            return Unit.Value;

        _telemetryService?.SendEvent(TelemetryEvents.DefineStepsCommandExecuted, new());

        // The argument comes from the client, so treat it as untrusted: only a string is used, and
        // anything else (missing, an object, a number) just skips the reveal. Revealing the file is a
        // convenience, so it must never fail the command or lose the event sent above. It is also
        // skipped for a client that does not advertise window/showDocument, so the command's response
        // is never held open on a request that client will not answer.
        if (_clientIde.Facets.HonorsShowDocumentRequests
            && request.Arguments is [JValue { Type: JTokenType.String } uriArgument, ..]
            && uriArgument.Value<string>() is { Length: > 0 } uriText
            && _languageServer.ClientSettings.Capabilities?.Window?.ShowDocument is { IsSupported: true, Value.Support: true })
        {
            try
            {
                await _languageServer.Window.ShowDocument(
                    new ShowDocumentParams { Uri = DocumentUri.Parse(uriText), TakeFocus = true },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogVerbose($"DefineStepsTriggeredHandler: showDocument failed for {uriText}: {ex.Message}");
            }
        }

        return Unit.Value;
    }
}
