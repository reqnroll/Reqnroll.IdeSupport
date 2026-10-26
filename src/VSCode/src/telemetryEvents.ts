/**
 * Centralizes the telemetry event names this extension originates itself, mirroring
 * `TelemetryEvents.cs` in the shared `Reqnroll.IdeSupport.Common` project
 * (`src/Core/Reqnroll.IdeSupport.Common/Telemetry/TelemetryEvents.cs`).
 * Keep the two lists in sync when adding or renaming an event.
 *
 * Server-originated events (`telemetry/event` notifications forwarded by
 * `src/telemetry.ts`'s `registerTelemetry`) don't need an entry here — this module is only
 * for events the VS Code client emits directly, where only the client is the source of truth.
 */
export const TelemetryEvents = {
  /** A genuine "Go to Step Definition" navigation: `doGoToStepDefinition`, or F12/Ctrl+click once the editor lands on a `textDocument/definition` target (`lsp/definitionNavigation.ts`; the request alone also fires on hover, so the server counts it as a lookup); `LocationCount`. */
  commandGoToStepDefinition: 'Command.GoToStepDefinition',
  /** A genuine "Go to Hooks" navigation via `doGoToHooks` — emitted client-side because the server's `reqnroll/findHooks` handler also serves CodeLens prefetches. */
  commandGoToHook: 'Command.GoToHook',
  /** A clickable tag's link was followed, via `openTagLink`. No properties: the URL and tag text are never sent. */
  commandTagLink: 'Command.TagLink',
  /** The language server failed to start or complete its handshake. `Reason`, `AttemptNumber`. */
  serverProcessStartFailed: 'Server.Process.StartFailed',
  /** A running language server stopped without the client asking it to. `Reason`, `AttemptNumber`. */
  serverProcessExitedUnexpectedly: 'Server.Process.ExitedUnexpectedly',
  /** The client started the language server again after a failure or exit. `Reason`, `AttemptNumber`. */
  serverProcessRestarted: 'Server.Process.Restarted',
  /** An exception thrown in this extension's own code: the same event the server sends for its exceptions; `ExceptionOrigin = "Client"` marks client-side ones. */
  errorUnhandledException: 'Error.UnhandledException',
  /** Extension activated (VS's `Lifecycle.Extension.Loaded`). */
  lifecycleExtensionLoaded: 'Lifecycle.Extension.Loaded',
  /** First activation ever for this user. */
  lifecycleExtensionInstalled: 'Lifecycle.Extension.Installed',
  /** First activation after the version increased. `OldExtensionVersion`. */
  lifecycleExtensionUpgraded: 'Lifecycle.Extension.Upgraded',
  /** Daily-active heartbeat on the first activation of each new day after install. `UsageDays`. */
  lifecycleUsageRecorded: 'Lifecycle.Usage.Recorded',
} as const;

/**
 * Closed set of `Reason` values for the server-lifecycle events; mirrors `ServerFailureReason` in
 * `ServerLifecycleTelemetry.cs`. Never free text.
 */
export const ServerFailureReason = {
  executableNotFound: 'ExecutableNotFound',
  startFailed: 'StartFailed',
  processExited: 'ProcessExited',
  sessionEnded: 'SessionEnded',
} as const;

/**
 * Property keys of client-originated events, mirroring `TelemetryProperties` in the LSP server
 * (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Telemetry/TelemetryProperties.cs`). PascalCase on the wire.
 */
export const TelemetryProperties = {
  /**
   * Event-scoped key: on "Command.GoToHook" it is the enum below; the server's
   * `TelemetryProperties.Source` (same literal) is a class name on `UnhandledException`. Keep the
   * literal in step with `GoToHookSources.PropertyName` (C#) and Rider's `GO_TO_HOOK_SOURCE_PROPERTY`.
   * How a "Go to Hooks" navigation was started; one of {@link GoToHookSource}.
   */
  source: 'Source',
  /** On "Command.GoToStepDefinition": navigable bindings offered (0 when there was nowhere to go); same key as VS. */
  locationCount: 'LocationCount',
  /** On "Lifecycle.Extension.Upgraded": the version the user upgraded from (same key as VS). */
  oldExtensionVersion: 'OldExtensionVersion',
  /** On "Lifecycle.Usage.Recorded": the running count of days of use (same key as VS). */
  usageDays: 'UsageDays',
} as const;

/** Closed set of `Source` values on "Command.GoToHook"; identical in VS, VS Code and Rider. */
export const GoToHookSource = {
  /** Command palette, keybinding or menu command with no editor-menu context. */
  command: 'Command',
  /** The editor right-click context menu. */
  contextMenu: 'ContextMenu',
  /** A click on the hook-count CodeLens. */
  codeLens: 'CodeLens',
} as const;

export type GoToHookSource = (typeof GoToHookSource)[keyof typeof GoToHookSource];
