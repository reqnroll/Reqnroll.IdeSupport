import * as vscode from 'vscode';
import { TelemetryEvents, TelemetryProperties, daysOfUsageEventName } from './telemetryEvents';

/** Persisted per-user lifecycle state; the VS Code counterpart of VS's `ReqnrollInstallationStatus`. */
export interface LifecycleState {
  installedVersion?: string;
  lastUsedDate?: string;
  usageDays: number;
}

export interface LifecycleEvent {
  eventName: string;
  properties?: Record<string, string>;
}

export const LIFECYCLE_STATE_KEY = 'reqnroll.telemetry.lifecycle';

/** `yyyy-MM-dd` in local time, matching VS's `DateTime.Today` day boundary. */
export function localDateKey(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

/** Numeric dotted-version comparison; non-numeric segments count as 0. */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map((s) => parseInt(s, 10) || 0);
  const pb = b.split('.').map((s) => parseInt(s, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const diff = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (diff !== 0) return diff < 0 ? -1 : 1;
  }
  return 0;
}

/**
 * Pure port of the decision logic in VS's `WelcomeService.OnIdeScopeActivityStarted` (issue #875):
 * `Extension loaded` on every activation, `Extension installed` on the first ever, `Extension upgraded`
 * when the version increased, and a `"{N} day usage"` heartbeat on the first activation of each new day
 * (not on the install day, like VS).
 */
export function computeLifecycle(
  state: LifecycleState,
  today: string,
  currentVersion: string,
): { state: LifecycleState; events: LifecycleEvent[] } {
  const events: LifecycleEvent[] = [{ eventName: TelemetryEvents.extensionLoaded }];

  if (!state.installedVersion) {
    events.push({ eventName: TelemetryEvents.extensionInstalled });
    return {
      state: { installedVersion: currentVersion, lastUsedDate: today, usageDays: 0 },
      events,
    };
  }

  const next: LifecycleState = { ...state };
  if (state.lastUsedDate !== today) {
    next.usageDays = state.usageDays + 1;
    next.lastUsedDate = today;
    events.push({ eventName: daysOfUsageEventName(next.usageDays) });
  }
  if (compareVersions(state.installedVersion, currentVersion) < 0) {
    events.push({
      eventName: TelemetryEvents.extensionUpgraded,
      properties: { [TelemetryProperties.oldExtensionVersion]: state.installedVersion },
    });
    next.installedVersion = currentVersion;
  }
  return { state: next, events };
}

/** Reads/updates the `globalState` lifecycle record and sends the resulting events. Never throws. */
export async function reportExtensionLifecycle(
  context: vscode.ExtensionContext,
  send: (eventName: string, properties?: Record<string, string>) => void,
  now: Date = new Date(),
): Promise<void> {
  try {
    const version = (context.extension.packageJSON as { version?: unknown }).version;
    if (typeof version !== 'string') return;

    const stored = context.globalState.get<LifecycleState>(LIFECYCLE_STATE_KEY);
    const { state, events } = computeLifecycle(
      stored ?? { usageDays: 0 },
      localDateKey(now),
      version,
    );
    await context.globalState.update(LIFECYCLE_STATE_KEY, state);
    for (const e of events) send(e.eventName, e.properties);
  } catch {
    // Telemetry must never surface to the user.
  }
}
