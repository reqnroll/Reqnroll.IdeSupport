import type * as vscode from 'vscode';
import { ReqnrollMethods } from './lsp/lspMethods';
import { TelemetryEvents } from './telemetryEvents';

/** Upper bound on events held while no Reqnroll project has been reported. */
export const MAX_HELD_EVENTS = 200;

/**
 * Errors, server start failures, and install/upgrade: sent even before a Reqnroll project is seen,
 * because they are worth knowing about whatever the workspace contains. Consent and opt-out still
 * apply to them.
 */
export function isAlwaysAllowed(eventName: string): boolean {
  return (
    eventName.startsWith('Error.') ||
    eventName === TelemetryEvents.lifecycleExtensionInstalled ||
    eventName === TelemetryEvents.lifecycleExtensionUpgraded ||
    eventName === TelemetryEvents.serverProcessStartFailed ||
    eventName === TelemetryEvents.serverProcessExitedUnexpectedly ||
    eventName === TelemetryEvents.serverProcessConnectionLost
  );
}

/**
 * Holds this host's telemetry until the language server reports that the workspace contains a
 * project that uses Reqnroll (`reqnroll/reqnrollProjectDetected`), so a workspace without Reqnroll
 * sends nothing but errors. The counterpart of `ReqnrollProjectTelemetryGate.cs` in VS and
 * `ReqnrollProjectTelemetryGate.kt` in Rider.
 *
 * Every event goes through `send`, the ones the server relays included: the server sends the
 * notification before anything it reports about the Reqnroll project, so the gate is open by the
 * time those arrive. Held events are sent in order on `open`; past `MAX_HELD_EVENTS` they are
 * dropped, and so is whatever is still held when the window closes. Once open it stays open.
 */
export class ReqnrollProjectGate {
  private held: (() => void)[] | undefined = [];
  private readonly openedListeners: (() => void)[] = [];

  get isOpen(): boolean {
    return this.held === undefined;
  }

  send(eventName: string, send: () => void): void {
    if (this.held && !isAlwaysAllowed(eventName)) {
      if (this.held.length < MAX_HELD_EVENTS) this.held.push(send);
      return;
    }
    send();
  }

  /** Runs `listener` once when the gate opens, or right away if it is already open. */
  onOpened(listener: () => void): void {
    if (this.isOpen) listener();
    else this.openedListeners.push(listener);
  }

  open(): void {
    const held = this.held;
    if (!held) return;
    this.held = undefined;
    for (const send of held) runSafely(send);
    for (const listener of this.openedListeners.splice(0)) runSafely(listener);
  }
}

function runSafely(action: () => void): void {
  try {
    action();
  } catch {
    // Telemetry must never surface to the user, and one failure must not stop the rest.
  }
}

/** The gate shared by everything in this extension host. */
export const reqnrollProjectGate = new ReqnrollProjectGate();

/** Subscribes to notifications by method name; satisfied by `LanguageClient`. */
export interface NotificationSource {
  onNotification(method: string, handler: (params: unknown) => void): vscode.Disposable;
}

/** Opens `gate` when the server sends `reqnroll/reqnrollProjectDetected`. */
export function registerReqnrollProjectDetected(
  client: NotificationSource,
  gate: ReqnrollProjectGate = reqnrollProjectGate,
): vscode.Disposable {
  return client.onNotification(ReqnrollMethods.reqnrollProjectDetected, () => gate.open());
}
