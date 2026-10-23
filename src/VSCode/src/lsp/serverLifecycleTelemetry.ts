import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import type { LanguageClient } from 'vscode-languageclient/node';
import { ServerFailureReason, TelemetryEvents } from '../telemetryEvents';
import type { ServerProcessTracker } from './serverProcessTracker';

type Send = (eventName: string, properties: Record<string, string | number>) => void;

/** How long an unexpected exit waits for the process to finish closing before it is reported with what is known. */
export const EXIT_DETAILS_TIMEOUT_MS = 2000;

/**
 * Reports the client-originated server-lifecycle events from the language client's
 * state transitions: a dead server cannot report itself, so the extension does.
 *
 * - `Server.Process.StartFailed`: a start attempt ended (`Stopped`) before the client ever reached `Running`,
 *   or `client.start()` rejected.
 * - `Server.Process.ExitedUnexpectedly`: `Running` to `Stopped` that was not requested via
 *   {@link markIntentionalStop}.
 * - `Server.Process.Restarted`: a new `Starting` after a failure or exit (vscode-languageclient restarts a
 *   crashed server itself) — `Reason` says what preceded it.
 *
 * `AttemptNumber` is the 1-based start attempt in this extension session. Each attempt reports at most
 * one failure event, however many of those signals fire for it.
 *
 * With a {@link ServerProcessTracker}, a start failure also carries `ServerSessionId`, and an unexpected exit
 * carries `ServerSessionId`, `ExitCode`/`ExitSignal`, `UptimeMs` and the crash report the runtime printed to
 * stderr; that event is sent once the process has finished closing (bounded by {@link EXIT_DETAILS_TIMEOUT_MS}).
 */
export class ServerLifecycleTelemetry implements vscode.Disposable {
  private readonly _listener: vscode.Disposable;
  private _attempt = 0;
  private _reachedRunning = false;
  private _failureReported = false;
  private _intentionalStop = false;
  private _lastFailureReason: string | undefined;

  constructor(
    client: Pick<LanguageClient, 'onDidChangeState'> &
      Partial<Pick<LanguageClient, 'serverProcess'>>,
    private readonly _send: Send,
    private readonly _tracker?: ServerProcessTracker,
  ) {
    this._listener = client.onDidChangeState((event) => {
      switch (event.newState) {
        case State.Starting:
          // Before anything else: it gives the process about to be spawned its session id.
          this._tracker?.onStarting();
          this._onStarting();
          break;
        case State.Running:
          this._reachedRunning = true;
          this._tracker?.onRunning(client.serverProcess);
          break;
        case State.Stopped:
          this._onStopped();
          break;
      }
    });
  }

  /** Call before deliberately stopping the client (deactivation) so the resulting `Stopped` is not a failure. */
  markIntentionalStop(): void {
    this._intentionalStop = true;
  }

  /** Call when `client.start()` rejects; a no-op if the state change already reported this attempt's failure. */
  reportStartRejected(): void {
    // A client that already reached Running started fine: a later throw is not a start failure.
    if (this._intentionalStop || this._failureReported || this._reachedRunning) return;
    this._attempt = Math.max(this._attempt, 1);
    this._reportFailure(TelemetryEvents.serverProcessStartFailed, ServerFailureReason.startFailed);
  }

  dispose(): void {
    this._listener.dispose();
  }

  private _onStarting(): void {
    this._attempt += 1;
    this._reachedRunning = false;
    this._failureReported = false;
    if (this._attempt > 1) {
      this._send(TelemetryEvents.serverProcessRestarted, {
        Reason: this._lastFailureReason ?? ServerFailureReason.sessionEnded,
        AttemptNumber: this._attempt,
      });
      this._lastFailureReason = undefined;
    }
  }

  private _onStopped(): void {
    if (this._intentionalStop) return;
    if (this._reachedRunning) {
      this._reachedRunning = false;
      this._reportFailure(
        TelemetryEvents.serverProcessExitedUnexpectedly,
        ServerFailureReason.processExited,
      );
    } else if (!this._failureReported) {
      this._reportFailure(
        TelemetryEvents.serverProcessStartFailed,
        ServerFailureReason.startFailed,
      );
    }
  }

  private _reportFailure(eventName: string, reason: string): void {
    this._failureReported = true;
    this._lastFailureReason = reason;
    const properties = { Reason: reason, AttemptNumber: this._attempt };
    if (!this._tracker) {
      this._send(eventName, properties);
    } else if (eventName === TelemetryEvents.serverProcessExitedUnexpectedly) {
      void this._tracker
        .describeExit(EXIT_DETAILS_TIMEOUT_MS)
        .catch(() => ({}))
        .then((details) => this._send(eventName, { ...details, ...properties }))
        // Telemetry must never surface as an unhandled rejection in the extension host.
        .catch(() => undefined);
    } else {
      this._send(eventName, { ...this._tracker.startDetails(), ...properties });
    }
  }
}
