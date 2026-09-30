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
} as const;
