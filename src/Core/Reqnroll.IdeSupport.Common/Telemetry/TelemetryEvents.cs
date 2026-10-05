namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Catalog of every Reqnroll telemetry event name, in one place, covering all .NET layers of
/// the solution (issue #797).
/// <para>
/// Two emitters use this catalog:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>LSP server</b> — events sent via <c>ILspTelemetryService.SendEvent</c> as
/// <c>telemetry/event</c> notifications (previously they lived in
/// <c>Reqnroll.IdeSupport.LSP.Server.Telemetry.TelemetryEvents</c>). The server project was the
/// original home of this catalog (issue #627), where each call site used to type its own
/// literal with no consistent shape: most followed <c>"&lt;PascalCaseWord&gt; command executed"</c>,
/// but one broke that with a bare space ("Reqnroll Discovery executed") and another was a
/// single generic word ("Error") one collision away from swallowing every other error-shaped
/// event in aggregate reporting. Referencing these constants instead of literals means a typo
/// or an accidental rename shows up as a compile error, not a silently-orphaned telemetry event.
/// </description></item>
/// <item><description>
/// <b>Visual Studio host</b> — events built as <c>VsGenericEvent</c>/<c>GenericEvent</c> and
/// handed to <c>ITelemetryTransmitter</c> (install/upgrade lifecycle, project wizard telemetry,
/// link clicks, the <c>VsWellKnownIdsSelfCheck</c> alert, and the client-originated "GoToHook
/// command executed" navigation event). These previously were inline literals inside
/// <c>TelemetryService.Monitor*</c> call sites across
/// <c>Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration</c> and the Extension project.
/// </description></item>
/// </list>
/// <para>
/// All events land in the <b>same</b> Application Insights resource, so the names form one
/// flat namespace regardless of which component emitted them. There are no event IDs —
/// Application Insights (and the legacy Trace/EventData contract) identifies events by name,
/// so the name <em>is</em> the identity; the property dictionaries below are the schema.
/// </para>
/// <para>
/// <b>Non-.NET clients mirror this catalog by hand:</b> VS Code keeps a copy in
/// <c>src/VSCode/src/telemetryEvents.ts</c> and Rider in
/// <c>ReqnrollTelemetryTransmitter.kt</c> (client-originated events only — both clients merely
/// forward server events without switching on their names). Keep those mirrors in sync when
/// adding or renaming an event.
/// </para>
/// <para>
/// <b>Not catalogued here:</b> the server's sampled <c>PerfSample</c> event, a named const in
/// <c>Reqnroll.IdeSupport.LSP.Server.Performance.OperationDurationRecorder.PerfSampleEventName</c>
/// that other code references and is deliberately left beside the recorder rather than
/// duplicated here. Exception <em>transmission</em> (VS host <c>TrackException</c>) is likewise
/// not an event name — see the design doc for how exceptions are reported.
/// </para>
/// </summary>
public static class TelemetryEvents
{
    // ── LSP server events (telemetry/event via ILspTelemetryService.SendEvent) ──────────────

    /// <summary>
    /// Sent after a binding-discovery run completes. Two emitters in the LSP server share the
    /// name and discriminate with the <c>DiscoverySource</c> property: the Roslyn syntax parser
    /// (<c>Discovery.Roslyn.CSharpBindingDiscoveryService</c>, triggered by a <c>.cs</c> file
    /// opening or edit) and the out-of-proc Connector
    /// (<c>Discovery.Connector.ConnectorBindingRegistryProvider</c>, triggered by project load or
    /// post-build refresh).
    /// </summary>
    /// <remarks>Renamed from <c>"Reqnroll Discovery executed"</c> (issue #627) — see the schema note
    /// in the design doc; historical App Insights data under the old name will not line up.</remarks>
    public const string ReqnrollDiscoveryExecuted = "ReqnrollDiscoveryExecuted";

    /// <summary>Sent by <c>Telemetry.LspErrorTelemetryService.MonitorError</c> for every reported exception.</summary>
    /// <remarks>Renamed from the bare, collision-prone <c>"Error"</c> (issue #627) — see the schema note
    /// in the design doc. The message is scrubbed for filesystem paths before transmission. The VS Code and Rider
    /// clients also send this event for exceptions in their own code (issue #621), marked <c>ExceptionOrigin = "Client"</c>;
    /// <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c> mirror the name.</remarks>
    public const string UnhandledException = "UnhandledException";

    /// <summary>Sent by <c>Features.Commenting.CommentToggleHandler</c> after handling a comment/uncomment request.</summary>
    public const string CommentUncommentCommandExecuted = "CommentUncomment command executed";

    /// <summary>Sent by <c>Features.Definition.FindMatchingScenariosHandler</c> after handling a Go To Matching Scenarios request.</summary>
    public const string GoToMatchingScenariosCommandExecuted = "GoToMatchingScenarios command executed";

    /// <summary>Sent by <c>Features.Formatting.FormattingHandler</c> after handling a document/range/on-type formatting request.</summary>
    public const string AutoFormatDocumentCommandExecuted = "AutoFormatDocument command executed";

    /// <summary>Sent by <c>Features.TestTargets.ResolveTestTargetsHandler</c> after resolving test targets for a Run request.</summary>
    public const string ResolveTestTargetsCommandExecuted = "ResolveTestTargets command executed";

    /// <summary>Sent by <c>Features.TestTargets.ResolveContainerTestTargetsHandler</c> after resolving test targets for a "Run scenarios" (Feature/Rule) request.</summary>
    public const string ResolveContainerTestTargetsCommandExecuted = "ResolveContainerTestTargets command executed";

    /// <summary>
    /// Sent unconditionally by <c>Features.Definition.FindHooksHandler</c> after handling any
    /// <c>reqnroll/findHooks</c> request — including the classic VS CodeLens's Details-popup
    /// prefetch, which calls the same handler on every lens render, not just on a click.
    /// </summary>
    /// <remarks>
    /// Renamed from <c>"GoToHook command executed"</c> (issue #698): that name implied every
    /// request was a genuine user navigation, which the CodeLens prefetch call is not — this
    /// event now reports only that a findHooks lookup ran, honestly matching what the server
    /// can actually observe. Real "the user navigated to a hook" telemetry is instead originated
    /// client-side, at each IDE's own navigation-command call site (<see cref="GoToHookCommandExecuted"/>),
    /// since only the client knows whether a given call is a genuine navigation or a CodeLens data-fetch.
    /// </remarks>
    public const string FindHooksCommandExecuted = "FindHooks command executed";

    /// <summary>Sent by <c>Features.CodeActions.CodeActionHandler</c> when the "Define step" quick fix is offered.</summary>
    public const string DefineStepsCommandOffered = "DefineSteps command offered";

    /// <summary>Sent by <c>Features.CodeActions.DefineStepsTriggeredHandler</c> when the client runs the command of a "Define step(s)" quick fix, i.e. the user picked it (issue #847).</summary>
    public const string DefineStepsCommandExecuted = "DefineSteps command executed";

    /// <summary>Sent by <c>Features.CodeActions.CodeActionHandler</c> when "Insert '&lt;keyword&gt;'" quick fixes are offered for a parser error.</summary>
    public const string InsertKeywordCommandOffered = "InsertKeyword command offered";

    /// <summary>Sent by <c>Features.CodeActions.InsertKeywordTriggeredHandler</c> when the client runs the command of an "Insert '&lt;keyword&gt;'" quick fix, i.e. the user picked it (issue #877).</summary>
    public const string InsertKeywordCommandExecuted = "InsertKeyword command executed";

    /// <summary>Sent by <c>Features.FindUnusedStepDefinitions.FindUnusedStepDefinitionsHandler</c> after handling a Find Unused Step Definitions request.</summary>
    public const string FindUnusedStepDefinitionsCommandExecuted = "FindUnusedStepDefinitions command executed";

    /// <summary>Sent by <c>Features.Rename.RenameHandler</c> after handling a Rename Step request (every terminal path, success or rejection).</summary>
    public const string RenameStepCommandExecuted = "Rename step command executed";

    /// <summary>
    /// Sent by <c>Features.References.FindStepUsagesHandler</c> (over the custom
    /// <c>reqnroll/findStepUsages</c> request) and by <c>Features.References.ReferencesHandler</c>
    /// (over the standard <c>textDocument/references</c> request) — two paths for the same user
    /// command, one event so the usage-count metric isn't split across IDE clients (issue #581).
    /// </summary>
    public const string FindStepDefinitionUsagesCommandExecuted = "FindStepDefinitionUsages command executed";

    /// <summary>
    /// A genuine Go To Step Definition navigation, sent by the client: the Visual Studio client when the user runs
    /// Go To Definition on a step (<c>GoToStepDefinitionPresenter</c>, issue #898) and the VS Code client for its
    /// "Go to Step Definition" picker command (issue #899). VS Code's F12/Ctrl+click and Rider's go-to-declaration
    /// use <c>textDocument/definition</c>, which also fires on hover, so they are not reported as navigations; the
    /// server counts them as <see cref="FindStepDefinitionsCommandExecuted"/>. Property: <c>LocationCount</c>.
    /// Mirrored verbatim in VS Code's <c>telemetryEvents.ts</c>.
    /// </summary>
    public const string GoToStepDefinitionCommandExecuted = "GoToStepDefinition command executed";

    /// <summary>
    /// Sent by <c>Features.Definition.FindStepDefinitionsHandler</c> for every <c>reqnroll/findStepDefinitions</c>
    /// lookup (issue #757) and by <c>Features.Definition.DefinitionHandler</c> for every <c>textDocument/definition</c>
    /// lookup from VS Code and Rider (issue #899; not Visual Studio, where that request is only a hover fall-through).
    /// Visual Studio sends the custom request on Go To Definition and on every Ctrl+hover that checks whether a
    /// word is navigable (issue #898), so read it as *lookups*, not navigations; the genuine navigation is
    /// <see cref="GoToStepDefinitionCommandExecuted"/>, sent by the client.
    /// </summary>
    public const string FindStepDefinitionsCommandExecuted = "FindStepDefinitions command executed";

    /// <summary>Sent by <c>Workspace.LspWorkspaceScopeManager</c> the first time a Reqnroll project is discovered in a workspace (issue #581 finding 2).</summary>
    public const string OpenProjectCommandExecuted = "OpenProject command executed";

    /// <summary>Sent by <c>Features.Rename.RenameTargetsHandler</c> after resolving the rename-target picker's candidate list (issue #581 finding 5).</summary>
    public const string RenameTargetsResolved = "RenameTargets resolved";

    /// <summary>
    /// Sent by <c>Features.TestOutcomes.TestOutcomeTcpListener</c> when a test run's
    /// <c>runComplete</c> message arrives over the bundled VSTest-logger socket, or the connection
    /// drops after the run started without one (sent as aborted, issue #848). Counts, flags, a
    /// duration and the target framework only — no paths, no test names, no content — plus which reporter sent the run,
    /// so MTP's ephemeral source-compiled reporter's real-world adoption is visible in
    /// aggregate (issue #722).
    /// </summary>
    public const string TestOutcomesRunCompleted = "TestOutcomesRunCompleted";

    /// <summary>
    /// Sent once per project, by <c>Features.Completions.FeatureTagIndex</c>, when the first tag-completion
    /// request has finished building that project's tag index — the one-time cost of scanning
    /// every <c>.feature</c> file the project owns (issue #828). Counts and a duration only — no
    /// project, file or tag names — so the real-world size of that scan, and how much of it was
    /// disk parsing, is visible in aggregate. Per-completion events are deliberately not sent
    /// (completion fires per keystroke); steady-state latency is covered by
    /// <c>PerfSample</c> under <c>textDocument/completion#tag</c>.
    /// </summary>
    public const string TagIndexFirstScanCompleted = "TagIndexFirstScanCompleted";

    /// <summary>
    /// Sent periodically, and once more at graceful shutdown, by
    /// <c>Performance.FeatureUsageFlushService</c> (issue #582): in-process counts of high-volume
    /// lookup and passive editor requests (completion, code actions, CodeLens, inlay hints, folding,
    /// outline, on-type formatting) that are too frequent to send as one event each. Discrete
    /// commands are not counted here — they send their own per-call events. Keys come from a closed
    /// catalogue and values are integers; no paths, text or document identifiers.
    /// </summary>
    public const string FeatureUsageSummary = "FeatureUsageSummary";

    /// <summary>
    /// Sent once per server process by <c>Telemetry.ServerSessionTelemetry</c> after the LSP handshake
    /// completes (issue #845): client version, OS family, CPU architecture, .NET runtime and
    /// <c>StartupMs</c> (process start to ready). IDE-agnostic replacement for the VS-only
    /// <see cref="ExtensionLoaded"/> for analytics: per-IDE active-session counts come from
    /// <c>IdeClient</c> and <c>SessionId</c>, and install/upgrade/daily-use are derived from it in
    /// Application Insights (distinct <c>ai.user.id</c> by first/last seen <c>ExtensionVersion</c>).
    /// </summary>
    public const string ServerSessionStarted = "ServerSessionStarted";

    /// <summary>
    /// Best-effort, sent once at graceful shutdown by <c>Telemetry.ServerSessionTelemetry</c> (issue #845)
    /// with <c>SessionSeconds</c>. Absence for a <c>SessionId</c> that has a <see cref="ServerSessionStarted"/>
    /// means the session ended abnormally (crash, force-quit); it follows the final <see cref="FeatureUsageSummary"/>.
    /// </summary>
    public const string ServerSessionEnded = "ServerSessionEnded";

    /// <summary>
    /// Sent by <c>Discovery.Connector.ConnectorBindingRegistryProvider</c> after each successful
    /// connector discovery run that changed the bindings (issue #845, from #258): one snapshot of
    /// step, hook (total and per hook type), binding-class and feature-file counts plus the target
    /// framework. Counts only; deliberately separate from <see cref="ReqnrollDiscoveryExecuted"/> so
    /// that event's schema stays stable.
    /// </summary>
    public const string ProjectCharacteristics = "ProjectCharacteristics";

    // ── Visual Studio host events (VsGenericEvent → ITelemetryTransmitter) ─────────────────

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorOpenProjectSystem</c>) when the extension activates inside an IDE scope.</summary>
    public const string ExtensionLoaded = "Extension loaded";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorOpenFeatureFile</c>) when a <c>.feature</c> file is opened.</summary>
    public const string FeatureFileOpened = "Feature file opened";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorExtensionInstalled</c>) on first activation after installation.</summary>
    public const string ExtensionInstalled = "Extension installed";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorExtensionUpgraded</c>) on first activation after an upgrade.</summary>
    public const string ExtensionUpgraded = "Extension upgraded";

    /// <summary>
    /// Format for the daily-active-usage heartbeat family of events: the actual event name is
    /// <c>string.Format(DaysOfUsageEventNameFormat, usageDays)</c>, producing e.g. <c>"7 day usage"</c>.
    /// A format (rather than a single constant) because the day count is part of the name —
    /// each distinct count is its own event in Application Insights.
    /// </summary>
    public const string DaysOfUsageEventNameFormat = "{0} day usage";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorCommandAddFeatureFile</c>) when a feature file is added via the command/wizard.</summary>
    public const string FeatureFileAdded = "Feature file added";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorCommandAddReqnrollConfigFile</c>) when a Reqnroll config file is added via the command/wizard.</summary>
    public const string ReqnrollConfigAdded = "Reqnroll config added";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorProjectTemplateWizardStarted</c>) when the "add new Reqnroll project" wizard starts.</summary>
    public const string ProjectTemplateWizardStarted = "Project Template Wizard Started";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorProjectTemplateWizardCompleted</c>) when the project-template wizard completes with the chosen options.</summary>
    public const string ProjectTemplateWizardCompleted = "Project Template Wizard Completed";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorLinkClicked</c>) when a link is clicked from extension UI, carrying the source and URL.</summary>
    public const string LinkClicked = "Link clicked";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorUpgradeDialogDismissed</c>) when the upgrade dialog is dismissed.</summary>
    public const string UpgradeDialogDismissed = "Upgrade dialog dismissed";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorWelcomeDialogDismissed</c>) when the welcome dialog is dismissed.</summary>
    public const string WelcomeDialogDismissed = "Welcome dialog dismissed";

    /// <summary>Sent by the VS extension (<c>VsWellKnownIdsSelfCheck</c>) when the startup self-check finds hard-coded well-known command IDs that do not resolve in this Visual Studio version (issue #779).</summary>
    public const string VsWellKnownIdsSelfCheckMismatch = "VsWellKnownIdsSelfCheckMismatch";

    // ── Client-originated events (one logical event, one copy per IDE) ─────────────────────

    /// <summary>
    /// Sent by each IDE client's own "Go to Hooks" navigation command when it fires — VS
    /// (<c>GoToHooksCommand</c>), VS Code (<c>src/VSCode/src/commands/goToHooks.ts</c>) and Rider
    /// (<c>GoToHooksRunner</c>) — for a genuine user navigation, as opposed to
    /// <see cref="FindHooksCommandExecuted"/>, which the server sends for every <c>reqnroll/findHooks</c>
    /// lookup including CodeLens prefetches (issue #698). Mirrored verbatim in
    /// <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>: keep the three copies in sync.
    /// </summary>
    public const string GoToHookCommandExecuted = "GoToHook command executed";

    /// <summary>
    /// Sent by each IDE client when the user opens a clickable Gherkin tag's link (issue #755) - VS
    /// (<c>TagLinkNavigableSymbolProvider</c>, Ctrl+Click), VS Code (<c>openTagLink</c>) and Rider
    /// (<c>ReqnrollFeatureTagLinkController</c>, Ctrl+Click). Only the client knows a link was actually followed:
    /// the server just answers <c>textDocument/documentLink</c> (counted as the passive <c>DocumentLink</c>
    /// feature). No properties: the target URL and tag text are user/repository data and are never sent.
    /// Mirrored verbatim in <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>: keep the three copies in sync.
    /// </summary>
    public const string TagLinkCommandExecuted = "TagLink command executed";

    /// <summary>
    /// Sent by each IDE client when the language server fails to start or to finish its handshake
    /// (issue #845): VS (<c>LspServerConnectionService</c>), VS Code (language-client start failure)
    /// and Rider (<c>ReqnrollServerLifecycleListener</c>, from <c>LspServerManagerListener</c> state changes). A dead server cannot report itself, so the client does.
    /// Properties: <c>Reason</c> (closed enum, see <c>ServerFailureReason</c>) and <c>AttemptNumber</c>.
    /// Mirrored verbatim in <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>.
    /// </summary>
    public const string ServerStartFailed = "ServerStartFailed";

    /// <summary>
    /// Sent by each IDE client when a running language server stops without the client having asked
    /// it to (issue #845). Properties: <c>Reason</c> (closed enum) and <c>AttemptNumber</c> (restarts so far).
    /// Client-side exception capture is separate (#621). Mirrored in the TS and Kotlin catalogs.
    /// </summary>
    public const string ServerExitedUnexpectedly = "ServerExitedUnexpectedly";

    /// <summary>
    /// Sent by each IDE client when it restarts the language server after a failure or unexpected
    /// exit (issue #845). Properties: <c>Reason</c> (closed enum) and <c>AttemptNumber</c>.
    /// Mirrored in the TS and Kotlin catalogs.
    /// </summary>
    public const string ServerRestarted = "ServerRestarted";
}
