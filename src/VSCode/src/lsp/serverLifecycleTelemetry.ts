import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import type { LanguageClient } from 'vscode-languageclient/node';
import { ServerFailureReason, TelemetryEvents } from '../telemetryEvents';

type Send = (eventName: string, properties: Record<string, string | number>) => void;

/**
 * Reports the client-originated server-lifecycle events (issue #845) from the language client's
 * state transitions: a dead server cannot report itself, so the extension does.
 *
 * - `ServerStartFailed`: a start attempt ended (`Stopped`) before the client ever reached `Running`,
 *   or `client.start()` rejected.
 * - `ServerExitedUnexpectedly`: `Running` to `Stopped` that was not requested via
 *   {@link markIntentionalStop}.
 * - `ServerRestarted`: a new `Starting` after a failure or exit (vscode-languageclient restarts a
 *   crashed server itself) — `Reason` says what preceded it.
 *
 * `AttemptNumber` is the 1-based start attempt in this extension session. Each attempt reports at most
 * one failure event, however many of those signals fire for it.
 */
export class ServerLifecycleTelemetry implements vscode.Disposable {
  private readonly _listener: vscode.Disposable;
  private _attempt = 0;
  private _reachedRunning = false;
  private _failureReported = false;
  private _intentionalStop = false;
  private _lastFailureReason: string | undefined;

  constructor(
    client: Pick<LanguageClient, 'onDidChangeState'>,
    private readonly _send: Send,
  ) {
    this._listener = client.onDidChangeState((event) => {
      switch (event.newState) {
        case State.Starting:
          this._onStarting();
          break;
        case State.Running:
          this._reachedRunning = true;
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
    if (this._intentionalStop || this._failureReported) return;
    this._attempt = Math.max(this._attempt, 1);
    this._reportFailure(TelemetryEvents.serverStartFailed, ServerFailureReason.startFailed);
  }

  dispose(): void {
    this._listener.dispose();
  }

  private _onStarting(): void {
    this._attempt += 1;
    this._reachedRunning = false;
    this._failureReported = false;
    if (this._attempt > 1) {
      this._send(TelemetryEvents.serverRestarted, {
        Reason: this._lastFailureReason ?? ServerFailureReason.userRestart,
        AttemptNumber: this._attempt,
      });
      this._lastFailureReason = undefined;
    }
  }

  private _onStopped(): void {
    if (this._intentionalStop) return;
    if (this._reachedRunning) {
      this._reachedRunning = false;
      this._reportFailure(TelemetryEvents.serverExitedUnexpectedly, ServerFailureReason.processExited);
    } else if (!this._failureReported) {
      this._reportFailure(TelemetryEvents.serverStartFailed, ServerFailureReason.startFailed);
    }
  }

  private _reportFailure(eventName: string, reason: string): void {
    this._failureReported = true;
    this._lastFailureReason = reason;
    this._send(eventName, { Reason: reason, AttemptNumber: this._attempt });
  }
}
