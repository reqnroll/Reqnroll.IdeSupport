#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
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

    /// <summary>
    /// Sends the event and its properties to the LSP client as a <c>telemetry/event</c> notification.
    /// The properties travel as a <see cref="JObject"/>, not a dictionary: the LSP serializer
    /// camelCases dictionary keys (<c>IdeClient</c> would arrive as <c>ideClient</c>), but never
    /// rewrites the names of a <see cref="JObject"/>, so every host forwards the PascalCase names
    /// the schema documents (issue #844).
    /// </summary>
    public void SendEvent(string eventName, Dictionary<string, object?> properties)
    {
        _languageServer.SendNotification(LspStandardMethodNames.TelemetryEvent, new
        {
            eventName,
            properties = ToJObject(TelemetryScrubber.ScrubProperties(properties))
        });
    }

    private static JObject ToJObject(Dictionary<string, object?> properties)
    {
        var result = new JObject();
        foreach (var (key, value) in properties)
            result[key] = value is null ? JValue.CreateNull() : JToken.FromObject(value);
        return result;
    }
}
