namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Catalog of every <c>telemetry/event</c> name sent via <see cref="ILspTelemetryService.SendEvent"/>
/// (issue #627). Previously each call site typed its own literal, with no consistent shape: most
/// followed <c>"&lt;PascalCaseWord&gt; command executed"</c>, but one broke that with a bare space
/// ("Reqnroll Discovery executed") and another was a single generic word ("Error") one collision
/// away from swallowing every other error-shaped event in aggregate reporting. Referencing these
/// constants instead of literals means a typo or an accidental rename shows up as a compile error,
/// not a silently-orphaned telemetry event.
/// </summary>
/// <remarks>
/// <see cref="Performance.OperationDurationRecorder.PerfSampleEventName"/> is the one call site that
/// already did this correctly (a named <c>const</c> other code references) and is left where it is
/// rather than duplicated here.
/// <para>
/// <b>Schema note:</b> <see cref="ReqnrollDiscoveryExecuted"/> and <see cref="UnhandledException"/>
/// carry <i>new</i> string values (previously <c>"Reqnroll Discovery executed"</c> and
/// <c>"Error"</c>) — a deliberate rename to fix the naming inconsistency, confirmed with the team
/// before merging since it means historical telemetry under the old names won't line up with new
/// data under these ones. Every other constant below keeps its existing transmitted value verbatim;
/// only the literal-vs-constant duplication was fixed for those.
/// </para>
/// </remarks>
public static class TelemetryEvents
{
    /// <summary>Sent by <see cref="Discovery.Roslyn.CSharpBindingDiscoveryService"/> and <see cref="Discovery.Connector.ConnectorBindingRegistryProvider"/> after a binding-discovery run completes.</summary>
    /// <remarks>Renamed from <c>"Reqnroll Discovery executed"</c> (issue #627) — see the schema note above.</remarks>
    public const string ReqnrollDiscoveryExecuted = "ReqnrollDiscoveryExecuted";

    /// <summary>Sent by <see cref="LspErrorTelemetryService.MonitorError"/> for every reported exception.</summary>
    /// <remarks>Renamed from the bare, collision-prone <c>"Error"</c> (issue #627) — see the schema note above.</remarks>
    public const string UnhandledException = "UnhandledException";

    /// <summary>Sent by <see cref="Features.Commenting.CommentToggleHandler"/> after handling a comment/uncomment request.</summary>
    public const string CommentUncommentCommandExecuted = "CommentUncomment command executed";

    /// <summary>Sent by <see cref="Features.Definition.GoToMatchingScenariosHandler"/> after handling a Go To Matching Scenarios request.</summary>
    public const string GoToMatchingScenariosCommandExecuted = "GoToMatchingScenarios command executed";

    /// <summary>Sent by <see cref="Features.Formatting.FormattingHandler"/> after handling a document/on-type formatting request.</summary>
    public const string AutoFormatDocumentCommandExecuted = "AutoFormatDocument command executed";

    /// <summary>Sent by <see cref="Features.TestTargets.ResolveTestTargetsHandler"/> after resolving test targets for a Run request.</summary>
    public const string ResolveTestTargetsCommandExecuted = "ResolveTestTargets command executed";

    /// <summary>Sent by <see cref="Features.Definition.GoToHooksHandler"/> after handling a Go To Hooks request.</summary>
    public const string GoToHookCommandExecuted = "GoToHook command executed";

    /// <summary>Sent by <see cref="Features.CodeActions.CodeActionHandler"/> when the "Define step" quick fix is offered.</summary>
    public const string DefineStepsCommandOffered = "DefineSteps command offered";

    /// <summary>Sent by <see cref="Features.FindUnusedStepDefinitions.FindUnusedStepDefinitionsHandler"/> after handling a Find Unused Step Definitions request.</summary>
    public const string FindUnusedStepDefinitionsCommandExecuted = "FindUnusedStepDefinitions command executed";

    /// <summary>Sent by <see cref="Features.Rename.RenameHandler"/> after handling a Rename Step request.</summary>
    public const string RenameStepCommandExecuted = "Rename step command executed";

    /// <summary>Sent by <see cref="Features.References.FindStepUsagesHandler"/> after handling a Find Step Usages request.</summary>
    public const string FindStepDefinitionUsagesCommandExecuted = "FindStepDefinitionUsages command executed";
}
