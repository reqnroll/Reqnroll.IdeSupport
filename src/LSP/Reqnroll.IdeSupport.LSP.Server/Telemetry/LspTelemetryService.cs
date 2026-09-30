#nullable enable

using System.Collections.Generic;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Sends <c>telemetry/event</c> notifications to the LSP client via
/// <see cref="ILanguageServerFacade.SendNotification"/>.
/// </summary>
public sealed class LspTelemetryService : ILspTelemetryService
{
    private readonly ILanguageServerFacade _languageServer;

    /// <summary>Initializes a new instance of the <see cref="LspTelemetryService"/> class.</summary>
    public LspTelemetryService(ILanguageServerFacade languageServer)
    {
        _languageServer = languageServer;
    }

    /// <summary>Sends the event and its properties to the LSP client as a <c>telemetry/event</c> notification.</summary>
    public void SendEvent(string eventName, Dictionary<string, object?> properties)
    {
        _languageServer.SendNotification(LspStandardMethodNames.TelemetryEvent, new
        {
            eventName,
            properties
        });
    }
}
