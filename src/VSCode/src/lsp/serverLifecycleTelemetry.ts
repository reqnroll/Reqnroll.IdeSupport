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
 * Whether a stop after `Running` was an exit or a lost connection is only known when those details arrive, but
 * vscode-languageclient restarts the server at once. A `Restarted` that follows such a stop therefore waits
 * for the stop to be classified and is sent right after its event, carrying the classified `Reason`.
 */
export class ServerLifecycleTelemetry implements vscode.Disposable {
  private readonly _listener: vscode.Disposable;
  private _attempt = 0;
  private _reachedRunning = false;
  private _failureReported = false;
  private _intentionalStop = false;
  private _lastFailureReason: string | undefined;
  /** The stop after `Running` still being classified; resolves, once its event is sent, to its `Reason`. */
  private _pendingStop: Promise<string> | undefined;

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
      const attemptNumber = this._attempt;
      const pendingStop = this._pendingStop;
      const reason = this._lastFailureReason ?? ServerFailureReason.sessionEnded;
      this._pendingStop = undefined;
      this._lastFailureReason = undefined;
      const sendRestarted = (reasonValue: string) =>
        this._send(TelemetryEvents.serverProcessRestarted, {
          Reason: reasonValue,
          AttemptNumber: attemptNumber,
        });
      if (pendingStop) {
        // Telemetry must never surface as an unhandled rejection in the extension host.
        void pendingStop.then(sendRestarted).catch(() => undefined);
      } else {
        sendRestarted(reason);
      }
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

    this._lastFailureReason = undefined;
    this._pendingStop = tracker
      .describeExit(EXIT_DETAILS_TIMEOUT_MS)
      .catch(() => ({}))
      .then((details) => {
        const exited = processEnded(details);
        const reason = exited
          ? ServerFailureReason.processExited
          : ServerFailureReason.connectionLost;
        this._send(
          exited
            ? TelemetryEvents.serverProcessExitedUnexpectedly
            : TelemetryEvents.serverProcessConnectionLost,
          { ...details, Reason: reason, AttemptNumber: attempt },
        );
        return reason;
      });
    // Nobody may ever wait on it (no restart follows), so a failure to send must not go unhandled.
    void this._pendingStop.catch(() => undefined);
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
