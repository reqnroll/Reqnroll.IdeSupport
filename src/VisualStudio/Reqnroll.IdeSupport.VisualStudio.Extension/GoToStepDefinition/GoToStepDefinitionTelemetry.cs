#nullable enable

using System.Collections.Generic;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Builds the client-originated "GoToStepDefinition command executed" event (issue #898): the user ran Go To
/// Definition (F12 or Ctrl+Click) in a <c>.feature</c> file. Only the client knows the command was run; the server
/// sees every Ctrl+hover lookup as <c>FindStepDefinitions command executed</c>. Kept on a plain static class for the
/// same unit-testability reason as <c>GoToHookTelemetry</c>.
/// </summary>
internal static class GoToStepDefinitionTelemetry
{
    /// <summary>Property: the number of navigable step-definition rows the command found (0 when there was nowhere to go).</summary>
    public const string LocationCountProperty = "LocationCount";

    public static GenericEvent CreateEvent(int navigableLocationCount) =>
        new(TelemetryEvents.GoToStepDefinitionCommandExecuted,
            [new KeyValuePair<string, object>(LocationCountProperty, navigableLocationCount)]);
}
