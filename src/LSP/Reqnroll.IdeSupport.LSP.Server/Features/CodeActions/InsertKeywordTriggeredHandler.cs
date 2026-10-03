using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <see cref="CommandName"/>, the <see cref="CodeAction.Command"/>
/// every "Insert '&lt;keyword&gt;'" quick fix carries. Clients run a code action's command after
/// applying its edit, so receiving it means the user picked the fix (issue #877) — it sends
/// <see cref="TelemetryEvents.InsertKeywordCommandExecuted"/>. This is the only acceptance signal the
/// server gets, because the action's <c>WorkspaceEdit</c> is applied entirely client-side.
/// </summary>
public sealed class InsertKeywordTriggeredHandler : IExecuteCommandHandler
{
    /// <summary>The command name carried by every Insert Keyword code action.</summary>
    public const string CommandName = "reqnroll.insertKeywordTriggered";

    private readonly ILspTelemetryService? _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="InsertKeywordTriggeredHandler"/> class.</summary>
    public InsertKeywordTriggeredHandler(ILspTelemetryService? telemetryService = null) =>
        _telemetryService = telemetryService;

    /// <summary>Advertises <see cref="CommandName"/> as an executable <c>workspace/executeCommand</c> command.</summary>
    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new() { Commands = new Container<string>(CommandName) };

    /// <summary>Records the trigger.</summary>
    public Task<Unit> Handle(ExecuteCommandParams request, CancellationToken cancellationToken)
    {
        if (request.Command == CommandName)
            _telemetryService?.SendEvent(TelemetryEvents.InsertKeywordCommandExecuted, new());

        return Task.FromResult(Unit.Value);
    }
}
