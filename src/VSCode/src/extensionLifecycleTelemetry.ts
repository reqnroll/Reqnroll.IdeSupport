import * as vscode from 'vscode';
import { TelemetryEvents, TelemetryProperties } from './telemetryEvents';

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
 * Install/upgrade half of VS's `WelcomeService` decision logic, run at every activation and sent
 * whether or not the workspace uses Reqnroll: `Lifecycle.Extension.Installed` on the first activation
 * ever (which also seeds the state, so the install day is not counted as a usage day, like VS), and
 * `Lifecycle.Extension.Upgraded` (property `OldExtensionVersion`) when the version increased. A
 * downgrade sends nothing.
 */
export function computeInstallOrUpgrade(
  state: LifecycleState,
  today: string,
  currentVersion: string,
): { state: LifecycleState; events: LifecycleEvent[] } {
  if (!state.installedVersion) {
    return {
      state: { installedVersion: currentVersion, lastUsedDate: today, usageDays: 0 },
      events: [{ eventName: TelemetryEvents.lifecycleExtensionInstalled }],
    };
  }
  if (compareVersions(state.installedVersion, currentVersion) < 0) {
    return {
      state: { ...state, installedVersion: currentVersion },
      events: [
        {
          eventName: TelemetryEvents.lifecycleExtensionUpgraded,
          properties: { [TelemetryProperties.oldExtensionVersion]: state.installedVersion },
        },
      ],
    };
  }
  return { state, events: [] };
}

/**
 * Usage half, run once the server reports a project that uses Reqnroll: `Lifecycle.Extension.Loaded`,
 * and a `Lifecycle.Usage.Recorded` heartbeat (property `UsageDays`) on the first such report of each
 * new local day. A day without a Reqnroll project is therefore not counted.
 */
export function computeUse(
  state: LifecycleState,
  today: string,
): { state: LifecycleState; events: LifecycleEvent[] } {
  const events: LifecycleEvent[] = [{ eventName: TelemetryEvents.lifecycleExtensionLoaded }];
  if (!state.installedVersion || state.lastUsedDate === today) return { state, events };

  const usageDays = state.usageDays + 1;
  events.push({
    eventName: TelemetryEvents.lifecycleUsageRecorded,
    properties: { [TelemetryProperties.usageDays]: String(usageDays) },
  });
  return { state: { ...state, usageDays, lastUsedDate: today }, events };
}

type Send = (eventName: string, properties?: Record<string, string>) => void;

// Both reports read and rewrite the same globalState record, and detection can arrive while the
// activation-time report is still awaiting its update, so they run one after the other.
let queue: Promise<void> = Promise.resolve();

function report(
  context: vscode.ExtensionContext,
  send: Send,
  compute: (
    state: LifecycleState,
    version: string,
  ) => { state: LifecycleState; events: LifecycleEvent[] },
): Promise<void> {
  const run = async (): Promise<void> => {
    try {
      const version = (context.extension.packageJSON as { version?: unknown }).version;
      if (typeof version !== 'string') return;

      const stored = context.globalState.get<LifecycleState>(LIFECYCLE_STATE_KEY);
      const { state, events } = compute(stored ?? { usageDays: 0 }, version);
      await context.globalState.update(LIFECYCLE_STATE_KEY, state);
      for (const e of events) send(e.eventName, e.properties);
    } catch {
      // Telemetry must never surface to the user.
    }
  };
  queue = queue.then(run);
  return queue;
}

/** Sends `Installed`/`Upgraded` when due and persists the state. Run at activation. Never throws. */
export function reportExtensionInstallOrUpgrade(
  context: vscode.ExtensionContext,
  send: Send,
  now: Date = new Date(),
): Promise<void> {
  return report(context, send, (state, version) =>
    computeInstallOrUpgrade(state, localDateKey(now), version),
  );
}

/** Sends `Loaded` and the daily heartbeat and persists the state. Run on Reqnroll-project detection. Never throws. */
export function reportExtensionUse(
  context: vscode.ExtensionContext,
  send: Send,
  now: Date = new Date(),
): Promise<void> {
  return report(context, send, (state) => computeUse(state, localDateKey(now)));
}
