import * as fs from 'fs';
import * as path from 'path';
import { resolveLogDirectory } from './logPaths';

/** Name of the environment variable that configures the telemetry debug log sink. */
export const TELEMETRY_DEBUG_LOG_ENV_VAR = 'REQNROLL_TELEMETRY_DEBUG_LOG';

/**
 * Debug-only sink that mirrors telemetry events to a local file for later review. Deliberately
 * independent of the analytics opt-out gate (`REQNROLL_TELEMETRY_ENABLED`): it records what the
 * extension *attempted* to send even when transmission is disabled or dropped downstream.
 * Mirrors the .NET side's `ITelemetryDebugLog` (`Reqnroll.IdeSupport.Common.Logging`), so a
 * support engineer sees VS Code's `"source":"host"` entries in the same shared
 * `reqnroll-telemetry-{date}.jsonl` as VS's and the LSP server's.
 */
export interface TelemetryDebugLog {
  /**
   * Appends one record describing a telemetry event.
   * @param source Where the event was captured: `"server"` or `"host"`.
   * @param eventName The telemetry event name.
   * @param properties The event properties; may be undefined.
   * @param enabled Host only: whether the opt-out gate allowed transmission.
   * @param transmitted Host only: whether the event was actually handed to the reporter.
   * @param error Host only: the message if transmission threw, otherwise undefined.
   */
  record(
    source: 'host' | 'server',
    eventName: string,
    properties?: Record<string, unknown>,
    enabled?: boolean,
    transmitted?: boolean,
    error?: string,
  ): void;
}

class NullTelemetryDebugLog implements TelemetryDebugLog {
  record(): void {
    // No sink configured.
  }
}

/** Appends one JSON-line record describing the telemetry event to a file. */
class FileTelemetryDebugLog implements TelemetryDebugLog {
  constructor(private readonly filePath: string) {}

  record(
    source: 'host' | 'server',
    eventName: string,
    properties?: Record<string, unknown>,
    enabled?: boolean,
    transmitted?: boolean,
    error?: string,
  ): void {
    try {
      const line = JSON.stringify({
        ts: new Date().toISOString(),
        source,
        event: eventName,
        props: properties ?? null,
        enabled: enabled ?? null,
        transmitted: transmitted ?? null,
        error: error ?? null,
      });

      const dir = path.dirname(this.filePath);
      fs.mkdirSync(dir, { recursive: true });
      fs.appendFileSync(this.filePath, line + '\n', 'utf8');
    } catch {
      // Debug logging must never break the extension.
    }
  }
}

/**
 * `<Reqnroll log dir>/reqnroll-telemetry-{yyyyMMdd}.jsonl` (UTC date) — a sibling of the existing
 * diagnostic logs written by `createGeneralLogChannel`, in the same `resolveLogDirectory()`
 * directory, and the same file name the .NET side's `TelemetryDebugLog.DefaultPath` and the
 * LSP server resolve to, so VS, VS Code and the server can share one file.
 */
export function defaultTelemetryDebugLogPath(): string {
  const date = new Date().toISOString().slice(0, 10).replace(/-/g, '');
  return path.join(resolveLogDirectory(), `reqnroll-telemetry-${date}.jsonl`);
}

/**
 * Resolves the sink from an explicit value (see {@link createTelemetryDebugLogFromEnvironment}):
 * unset / empty / `"0"` / `"false"` → disabled (no-op); `"1"` / `"true"` → JSONL at
 * {@link defaultTelemetryDebugLogPath}; any other value → treated as the target file path.
 */
export function createTelemetryDebugLogFromValue(value: string | undefined): TelemetryDebugLog {
  const trimmed = value?.trim();
  if (!trimmed) return new NullTelemetryDebugLog();
  if (trimmed === '0' || trimmed.toLowerCase() === 'false') return new NullTelemetryDebugLog();

  const filePath =
    trimmed === '1' || trimmed.toLowerCase() === 'true' ? defaultTelemetryDebugLogPath() : trimmed;
  return new FileTelemetryDebugLog(filePath);
}

/** Resolves the sink from the {@link TELEMETRY_DEBUG_LOG_ENV_VAR} environment variable. */
export function createTelemetryDebugLogFromEnvironment(): TelemetryDebugLog {
  return createTelemetryDebugLogFromValue(process.env[TELEMETRY_DEBUG_LOG_ENV_VAR]);
}
