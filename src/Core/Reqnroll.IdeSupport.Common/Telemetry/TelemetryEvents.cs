namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Catalog of every Reqnroll telemetry event name, in one place, covering all .NET layers of
/// the solution.
/// <para>
/// Two emitters use this catalog:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>LSP server</b> — events sent via <c>ILspTelemetryService.SendEvent</c> as
/// <c>telemetry/event</c> notifications. Names are hierarchical, <c>Category.Name</c> (or
/// <c>Category.Name.Qualifier</c>), PascalCase segments separated by dots. Categories:
/// <c>Command</c> (a user-invoked command ran), <c>Lookup</c> (a request the server answered that is not
/// necessarily a user action), <c>CodeAction</c> (a quick fix offered / picked), <c>Discovery</c>, <c>Project</c>,
/// <c>TestRun</c>, <c>TagIndex</c>, <c>Server</c> (process lifecycle), <c>Lifecycle</c> (extension install / upgrade / welcome and upgrade dialogs /
/// usage), <c>Usage</c>, <c>Perf</c>, <c>Error</c> and <c>Vs</c> (Visual Studio-only host events such as self-check failures). The category prefix is
/// what keeps a generic tail such as "Executed" or "Error" from colliding in aggregate reporting.
/// Referencing these constants instead of literals means a typo or an accidental rename shows up
/// as a compile error, not a silently-orphaned telemetry event.
/// </description></item>
/// <item><description>
/// <b>Visual Studio host</b> — events built as <c>VsGenericEvent</c>/<c>GenericEvent</c> and
/// handed to <c>ITelemetryTransmitter</c> (install/upgrade lifecycle, project wizard telemetry,
/// link clicks, the <c>VsWellKnownIdsSelfCheck</c> alert, and the client-originated <c>Command.GoToHook</c>
/// navigation event), sent from <c>TelemetryService.Monitor*</c> call sites across
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
/// <b>Not event names:</b> Exception <em>transmission</em> (VS host <c>TrackException</c>) is likewise
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
    /// <remarks>Earlier App Insights data was recorded under the old name <c>"Reqnroll Discovery executed"</c>
    /// and will not line up with this one — see the schema note in the design doc.</remarks>
    public const string ReqnrollDiscoveryExecuted = "Discovery.Executed";

    /// <summary>Sent by <c>Telemetry.LspErrorTelemetryService.MonitorError</c> for every reported exception.</summary>
    /// <remarks>Earlier App Insights data was recorded under the bare, collision-prone name <c>"Error"</c>
    /// — see the schema note in the design doc. The message is scrubbed for filesystem paths before transmission. The VS Code and Rider
    /// clients also send this event for exceptions in their own code, marked <c>ExceptionOrigin = "Client"</c>;
    /// <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c> mirror the name.</remarks>
    public const string UnhandledException = "Error.UnhandledException";

    /// <summary>Sent by <c>Features.Commenting.CommentToggleHandler</c> after handling a comment/uncomment request.</summary>
    public const string CommentUncommentCommandExecuted = "Command.CommentUncomment";

    /// <summary>Sent by <c>Features.Definition.FindMatchingScenariosHandler</c> after handling a Go To Matching Scenarios request.</summary>
    public const string GoToMatchingScenariosCommandExecuted = "Command.GoToMatchingScenarios";

    /// <summary>Sent by <c>Features.Formatting.FormattingHandler</c> after handling a document/range/on-type formatting request.</summary>
    public const string AutoFormatDocumentCommandExecuted = "Command.AutoFormatDocument";

    /// <summary>Sent by <c>Features.TestTargets.ResolveTestTargetsHandler</c> after resolving test targets for a Run request.</summary>
    public const string ResolveTestTargetsCommandExecuted = "Lookup.ResolveTestTargets";

    /// <summary>Sent by <c>Features.TestTargets.ResolveContainerTestTargetsHandler</c> after resolving test targets for a "Run scenarios" (Feature/Rule) request.</summary>
    public const string ResolveContainerTestTargetsCommandExecuted = "Lookup.ResolveContainerTestTargets";

    /// <summary>
    /// Sent unconditionally by <c>Features.Definition.FindHooksHandler</c> after handling any
    /// <c>reqnroll/findHooks</c> request — including the classic VS CodeLens's Details-popup
    /// prefetch, which calls the same handler on every lens render, not just on a click.
    /// </summary>
    /// <remarks>
    /// This event reports only that a findHooks lookup ran, which is all the server can observe;
    /// it must not be read as a genuine user navigation, since the CodeLens prefetch call is not one. Real "the user navigated to a hook" telemetry is instead originated
    /// client-side, at each IDE's own navigation-command call site (<see cref="GoToHookCommandExecuted"/>),
    /// since only the client knows whether a given call is a genuine navigation or a CodeLens data-fetch.
    /// </remarks>
    public const string FindHooksCommandExecuted = "Lookup.FindHooks";

    /// <summary>Sent by <c>Features.CodeActions.CodeActionHandler</c> when the "Define step" quick fix is offered.</summary>
    public const string DefineStepsCommandOffered = "CodeAction.DefineSteps.Offered";

    /// <summary>Sent by <c>Features.CodeActions.DefineStepsTriggeredHandler</c> when the client runs the command of a "Define step(s)" quick fix, i.e. the user picked it.</summary>
    public const string DefineStepsCommandExecuted = "CodeAction.DefineSteps.Executed";

    /// <summary>Sent by <c>Features.CodeActions.CodeActionHandler</c> when "Insert '&lt;keyword&gt;'" quick fixes are offered for a parser error.</summary>
    public const string InsertKeywordCommandOffered = "CodeAction.InsertKeyword.Offered";

    /// <summary>Sent by <c>Features.CodeActions.InsertKeywordTriggeredHandler</c> when the client runs the command of an "Insert '&lt;keyword&gt;'" quick fix, i.e. the user picked it.</summary>
    public const string InsertKeywordCommandExecuted = "CodeAction.InsertKeyword.Executed";

    /// <summary>Sent by <c>Features.FindUnusedStepDefinitions.FindUnusedStepDefinitionsHandler</c> after handling a Find Unused Step Definitions request.</summary>
    public const string FindUnusedStepDefinitionsCommandExecuted = "Command.FindUnusedStepDefinitions";

    /// <summary>Sent by <c>Features.Rename.RenameHandler</c> after handling a Rename Step request (every terminal path, success or rejection).</summary>
    public const string RenameStepCommandExecuted = "Command.RenameStep";

    /// <summary>
    /// Sent by <c>Features.References.FindStepUsagesHandler</c> (over the custom
    /// <c>reqnroll/findStepUsages</c> request) and by <c>Features.References.ReferencesHandler</c>
    /// (over the standard <c>textDocument/references</c> request) — two paths for the same user
    /// command, one event so the usage-count metric isn't split across IDE clients.
    /// </summary>
    public const string FindStepDefinitionUsagesCommandExecuted = "Command.FindStepDefinitionUsages";

    /// <summary>
    /// A genuine Go To Step Definition navigation, sent by the client: the Visual Studio client when the user runs
    /// Go To Definition on a step (<c>GoToStepDefinitionPresenter</c>) and the VS Code client for its
    /// "Go to Step Definition" picker command. VS Code's F12/Ctrl+click and Rider's go-to-declaration
    /// use <c>textDocument/definition</c>, which also fires on hover, so they are not reported as navigations; the
    /// server counts them as <see cref="FindStepDefinitionsCommandExecuted"/>. Property: <c>LocationCount</c>.
    /// Mirrored verbatim in VS Code's <c>telemetryEvents.ts</c>.
    /// </summary>
    public const string GoToStepDefinitionCommandExecuted = "Command.GoToStepDefinition";

    /// <summary>
    /// Sent by <c>Features.Definition.FindStepDefinitionsHandler</c> for every <c>reqnroll/findStepDefinitions</c>
    /// lookup and by <c>Features.Definition.DefinitionHandler</c> for every <c>textDocument/definition</c>
    /// lookup from VS Code and Rider (not Visual Studio, where that request is mostly the editor's hover fall-through).
    /// Visual Studio sends the custom request on Go To Definition and on every Ctrl+hover that checks whether a
    /// word is navigable, so read it as *lookups*, not navigations; the genuine navigation is
    /// <see cref="GoToStepDefinitionCommandExecuted"/>, sent by the client.
    /// </summary>
    public const string FindStepDefinitionsCommandExecuted = "Lookup.FindStepDefinitions";

    /// <summary>Sent by <c>Workspace.LspWorkspaceScopeManager</c> the first time a Reqnroll project is discovered in a workspace.</summary>
    public const string OpenProjectCommandExecuted = "Project.Opened";

    /// <summary>
    /// Sampled latency of a server operation (<c>Performance.OperationDurationRecorder</c>); ~5% by default, so
    /// read it as a latency distribution, not a usage count. Properties: <c>Operation</c>, <c>DurationMs</c>, <c>DurationBucket</c>.
    /// </summary>
    public const string PerfSample = "Perf.Sample";

    /// <summary>Sent by <c>Features.Rename.RenameTargetsHandler</c> after resolving the rename-target picker's candidate list.</summary>
    public const string RenameTargetsResolved = "Lookup.RenameTargets";

    /// <summary>
    /// Sent by <c>Features.TestOutcomes.TestOutcomeTcpListener</c> when a test run's
    /// <c>runComplete</c> message arrives over the bundled VSTest-logger socket, or the connection
    /// drops after the run started without one (sent as aborted). Counts, flags, a
    /// duration and the target framework only — no paths, no test names, no content — plus which reporter sent the run,
    /// so MTP's ephemeral source-compiled reporter's real-world adoption is visible in
    /// aggregate.
    /// </summary>
    public const string TestOutcomesRunCompleted = "TestRun.Completed";

    /// <summary>
    /// Sent once per project, by <c>Features.Completions.FeatureTagIndex</c>, when the first tag-completion
    /// request has finished building that project's tag index — the one-time cost of scanning
    /// every <c>.feature</c> file the project owns. Counts and a duration only — no
    /// project, file or tag names — so the real-world size of that scan, and how much of it was
    /// disk parsing, is visible in aggregate. Per-completion events are deliberately not sent
    /// (completion fires per keystroke); steady-state latency is covered by
    /// <c>PerfSample</c> under <c>textDocument/completion#tag</c>.
    /// </summary>
    public const string TagIndexFirstScanCompleted = "TagIndex.FirstScanCompleted";

    /// <summary>
    /// Sent periodically, and once more at graceful shutdown, by
    /// <c>Performance.FeatureUsageFlushService</c>: in-process counts of high-volume
    /// lookup and passive editor requests (completion, code actions, CodeLens, inlay hints, folding,
    /// outline, on-type formatting) that are too frequent to send as one event each. Discrete
    /// commands are not counted here — they send their own per-call events. Keys come from a closed
    /// catalogue and values are integers; no paths, text or document identifiers.
    /// </summary>
    public const string FeatureUsageSummary = "Usage.Summary";

    /// <summary>
    /// Sent once per server process by <c>Telemetry.ServerSessionTelemetry</c> after the LSP handshake
    /// completes: client version, OS family, CPU architecture, .NET runtime and
    /// <c>StartupMs</c> (process start to ready). IDE-agnostic replacement for the VS-only
    /// <see cref="ExtensionLoaded"/> for analytics: per-IDE active-session counts come from
    /// <c>IdeClient</c> and <c>SessionId</c>, and install/upgrade/daily-use are derived from it in
    /// Application Insights (distinct <c>ai.user.id</c> by first/last seen <c>ExtensionVersion</c>).
    /// </summary>
    public const string ServerSessionStarted = "Server.SessionStarted";

    /// <summary>
    /// Best-effort, sent once at graceful shutdown by <c>Telemetry.ServerSessionTelemetry</c>
    /// with <c>SessionSeconds</c>. Absence for a <c>SessionId</c> that has a <see cref="Server.SessionStarted"/>
    /// means the session ended abnormally (crash, force-quit); it follows the final <see cref="Usage.Summary"/>.
    /// </summary>
    public const string ServerSessionEnded = "Server.SessionEnded";

    /// <summary>
    /// Sent by <c>Discovery.Connector.ConnectorBindingRegistryProvider</c> after each successful
    /// connector discovery run that changed the bindings: one snapshot of
    /// step, hook (total and per hook type), binding-class and feature-file counts plus the target
    /// framework. Counts only; deliberately separate from <see cref="Discovery.Executed"/> so
    /// that event's schema stays stable.
    /// </summary>
    public const string ProjectCharacteristics = "Project.Characteristics";

    // ── Visual Studio host events (VsGenericEvent → ITelemetryTransmitter) ─────────────────

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorOpenProjectSystem</c>) when the extension activates inside an IDE scope.</summary>
    public const string ExtensionLoaded = "Lifecycle.Loaded";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorOpenFeatureFile</c>) when a <c>.feature</c> file is opened.</summary>
    public const string FeatureFileOpened = "Project.FeatureFileOpened";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorExtensionInstalled</c>) on first activation after installation.</summary>
    public const string ExtensionInstalled = "Lifecycle.Installed";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorExtensionUpgraded</c>) on first activation after an upgrade.</summary>
    public const string ExtensionUpgraded = "Lifecycle.Upgraded";

    /// <summary>
    /// Daily-active-usage heartbeat, sent by all three IDEs on the first activation of each new day
    /// after install. Property: <c>UsageDays</c> (the running count). Previously the count was part of
    /// the event name (<c>"7 day usage"</c>), so every count was its own event; query the legacy names
    /// through the <c>ReqnrollEvents()</c> function in <c>telemetry/queries</c>.
    /// </summary>
    public const string DaysOfUsage = "Lifecycle.DaysOfUsage";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorCommandAddFeatureFile</c>) when a feature file is added via the command/wizard.</summary>
    public const string FeatureFileAdded = "Project.FeatureFileAdded";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorCommandAddReqnrollConfigFile</c>) when a Reqnroll config file is added via the command/wizard.</summary>
    public const string ReqnrollConfigAdded = "Project.ReqnrollConfigAdded";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorProjectTemplateWizardStarted</c>) when the "add new Reqnroll project" wizard starts.</summary>
    public const string ProjectCreationStarted = "Project.CreationStarted";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorProjectTemplateWizardCompleted</c>) when the project-template wizard completes with the chosen options.</summary>
    public const string ProjectCreationCompleted = "Project.CreationCompleted";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorLinkClicked</c>) when a link is clicked from extension UI, carrying the source and URL.</summary>
    public const string LinkClicked = "Lifecycle.LinkClicked";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorUpgradeDialogDismissed</c>) when the upgrade dialog is dismissed.</summary>
    public const string UpgradeDialogDismissed = "Lifecycle.UpgradeDialogDismissed";

    /// <summary>Sent by the VS extension (<c>TelemetryService.MonitorWelcomeDialogDismissed</c>) when the welcome dialog is dismissed.</summary>
    public const string WelcomeDialogDismissed = "Lifecycle.WelcomeDialogDismissed";

    /// <summary>Sent by the VS extension (<c>VsWellKnownIdsSelfCheck</c>) when the startup self-check finds hard-coded well-known command IDs that do not resolve in this Visual Studio version.</summary>
    public const string VsWellKnownIdsSelfCheckMismatch = "Vs.WellKnownIdsSelfCheckMismatch";

    // ── Client-originated events (one logical event, one copy per IDE) ─────────────────────

    /// <summary>
    /// Sent by each IDE client's own "Go to Hooks" navigation command when it fires — VS
    /// (<c>GoToHooksCommand</c>), VS Code (<c>src/VSCode/src/commands/goToHooks.ts</c>) and Rider
    /// (<c>GoToHooksRunner</c>) — for a genuine user navigation, as opposed to
    /// <see cref="FindHooksCommandExecuted"/>, which the server sends for every <c>reqnroll/findHooks</c>
    /// lookup including CodeLens prefetches. Mirrored verbatim in
    /// <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>: keep the three copies in sync.
    /// </summary>
    public const string GoToHookCommandExecuted = "Command.GoToHook";

    /// <summary>
    /// Sent by each IDE client when the user opens a clickable Gherkin tag's link - VS
    /// (<c>TagLinkNavigableSymbolProvider</c>, Ctrl+Click), VS Code (<c>openTagLink</c>) and Rider
    /// (<c>ReqnrollFeatureTagLinkController</c>, Ctrl+Click). Only the client knows a link was actually followed:
    /// the server just answers <c>textDocument/documentLink</c> (counted as the passive <c>DocumentLink</c>
    /// feature). No properties: the target URL and tag text are user/repository data and are never sent.
    /// Mirrored verbatim in <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>: keep the three copies in sync.
    /// </summary>
    public const string TagLinkCommandExecuted = "Command.TagLink";

    /// <summary>
    /// Sent by each IDE client when the language server fails to start or to finish its handshake:
    /// VS (<c>LspServerConnectionService</c>), VS Code (language-client start failure)
    /// and Rider (<c>ReqnrollServerLifecycleListener</c>, from <c>LspServerManagerListener</c> state changes). A dead server cannot report itself, so the client does.
    /// Properties: <c>Reason</c> (closed enum, see <c>ServerFailureReason</c>) and <c>AttemptNumber</c>.
    /// Mirrored verbatim in <c>telemetryEvents.ts</c> and <c>RiderTelemetryTransmitter.kt</c>.
    /// </summary>
    public const string ServerStartFailed = "Server.StartFailed";

    /// <summary>
    /// Sent by each IDE client when a running language server stops without the client having asked
    /// it to. Properties: <c>Reason</c> (closed enum) and <c>AttemptNumber</c> (restarts so far).
    /// Client-side exception capture is separate. Mirrored in the TS and Kotlin catalogs.
    /// </summary>
    public const string ServerExitedUnexpectedly = "Server.ExitedUnexpectedly";

    /// <summary>
    /// Sent by each IDE client when it restarts the language server after a failure or unexpected
    /// exit. Properties: <c>Reason</c> (closed enum) and <c>AttemptNumber</c>.
    /// Mirrored in the TS and Kotlin catalogs.
    /// </summary>
    public const string ServerRestarted = "Server.Restarted";
}
