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
  /** A genuine "Go to Hooks" navigation via `doGoToHooks` (issue #698) — emitted client-side because the server's `reqnroll/findHooks` handler also serves CodeLens prefetches. */
  goToHookCommandExecuted: 'GoToHook command executed',
  /** An exception thrown in this extension's own code (issue #621): the same event the server sends for its exceptions; `ExceptionOrigin = "Client"` marks client-side ones. */
  unhandledException: 'UnhandledException',
} as const;

/**
 * Property keys of client-originated events, mirroring `TelemetryProperties` in the LSP server
 * (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Telemetry/TelemetryProperties.cs`). PascalCase on the wire.
 */
export const TelemetryProperties = {
  /**
   * Event-scoped key: on "GoToHook command executed" it is the enum below; the server's
   * `TelemetryProperties.Source` (same literal) is a class name on `UnhandledException`. Keep the
   * literal in step with `GoToHookSources.PropertyName` (C#) and Rider's `GO_TO_HOOK_SOURCE_PROPERTY`.
   * How a "Go to Hooks" navigation was started; one of {@link GoToHookSource}.
   */
  source: 'Source',
} as const;

/** Closed set of `Source` values on "GoToHook command executed"; identical in VS, VS Code and Rider. */
export const GoToHookSource = {
  /** Command palette, keybinding or menu command with no editor-menu context. */
  command: 'Command',
  /** The editor right-click context menu. */
  contextMenu: 'ContextMenu',
  /** A click on the hook-count CodeLens. */
  codeLens: 'CodeLens',
} as const;

export type GoToHookSource = (typeof GoToHookSource)[keyof typeof GoToHookSource];
