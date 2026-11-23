import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import type { LanguageClient } from 'vscode-languageclient/node';
import { ServerFailureReason, TelemetryEvents } from '../telemetryEvents';
import { CrashReportProperties } from './serverCrashReport';
import { ExitProperties, type ServerProcessTracker } from './serverProcessTracker';

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
 *   {@link markIntentionalStop}, when the process is known to have ended (an exit code or signal, or a crash report).
 * - `Server.Process.ConnectionLost`: the same transition when the process had not ended once its exit details
 *   were awaited. The client sees the connection close, not the process die, so this is a lost or stalled
 *   connection to a server that may still be alive rather than a crash.
 * - `Server.Process.Restarted`: a new `Starting` after a failure or exit (vscode-languageclient restarts a
 *   crashed server itself) — `Reason` says what preceded it.
 *
 * `AttemptNumber` is the 1-based start attempt in this extension session. Each attempt reports at most
 * one failure event, however many of those signals fire for it.
 *
 * With a {@link ServerProcessTracker}, a start failure also carries `ServerSessionId`, and an unexpected exit
 * carries `ServerSessionId`, `ExitCode`/`ExitSignal`, `UptimeMs` and the crash report the runtime printed to
 * stderr; that event is sent once the process has finished closing (bounded by {@link EXIT_DETAILS_TIMEOUT_MS}).
 *
 * Whether the process ended is only known when those details arrive, but a quick restart can come first, so
 * the stop is first recorded as a lost connection (the next `Restarted` reads it) and corrected to a process
 * exit if the details show the process ended and no newer attempt has started.
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
      this._reportStopAfterRunning();
    } else if (!this._failureReported) {
      this._reportFailure(
        TelemetryEvents.serverProcessStartFailed,
        ServerFailureReason.startFailed,
      );
    }
  }

  private _reportStopAfterRunning(): void {
    this._failureReported = true;
    const attempt = this._attempt;
    const tracker = this._tracker;
    if (!tracker) {
      // Nothing to tell whether the process ended: report it the way a stop always was.
      this._lastFailureReason = ServerFailureReason.processExited;
      this._send(TelemetryEvents.serverProcessExitedUnexpectedly, {
        Reason: ServerFailureReason.processExited,
        AttemptNumber: attempt,
      });
      return;
    }

    // Set before the details are known, so a restart that comes first still finds a reason.
    this._lastFailureReason = ServerFailureReason.connectionLost;
    void tracker
      .describeExit(EXIT_DETAILS_TIMEOUT_MS)
      .catch(() => ({}))
      .then((details) => {
        if (processEnded(details)) {
          if (
            this._attempt === attempt &&
            this._lastFailureReason === ServerFailureReason.connectionLost
          ) {
            this._lastFailureReason = ServerFailureReason.processExited;
          }
          this._send(TelemetryEvents.serverProcessExitedUnexpectedly, {
            ...details,
            Reason: ServerFailureReason.processExited,
            AttemptNumber: attempt,
          });
        } else {
          this._send(TelemetryEvents.serverProcessConnectionLost, {
            ...details,
            Reason: ServerFailureReason.connectionLost,
            AttemptNumber: attempt,
          });
        }
      })
      // Telemetry must never surface as an unhandled rejection in the extension host.
      .catch(() => undefined);
  }

  private _reportFailure(eventName: string, reason: string): void {
    this._failureReported = true;
    this._lastFailureReason = reason;
    const properties = { Reason: reason, AttemptNumber: this._attempt };
    this._send(
      eventName,
      this._tracker ? { ...this._tracker.startDetails(), ...properties } : properties,
    );
  }
}

/** An exit code, an exit signal or a crash report is evidence the process ended; without any it may still be running. */
function processEnded(details: Record<string, string | number>): boolean {
  return (
    ExitProperties.exitCode in details ||
    ExitProperties.exitSignal in details ||
    CrashReportProperties.crashKind in details
  );
}
