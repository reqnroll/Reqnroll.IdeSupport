#nullable enable

using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.DocumentLinks;

/// <summary>
/// Builds the client-originated "TagLink command executed" event (issue #755). It deliberately has no
/// properties: the target URL and tag text come from repository configuration and are never sent.
/// Kept on a plain static class for the same unit-testability reason as <c>GoToHookTelemetry</c>.
/// </summary>
internal static class TagLinkTelemetry
{
    public static GenericEvent CreateEvent() => new(TelemetryEvents.TagLinkCommandExecuted, []);
}
