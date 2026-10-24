import { randomUUID } from 'crypto';
import * as readline from 'readline';
import type { ChildProcess } from 'child_process';
import { CrashReportCollector, crashReportProperties } from './serverCrashReport';

/** Telemetry property name; mirrors `ServerSession.PropertyName` in `Reqnroll.IdeSupport.Common`. */
export const SERVER_SESSION_ID_PROPERTY = 'ServerSessionId';

/** Server command-line flag; mirrors `ServerSession.ServerArgumentName`. */
export const SERVER_SESSION_ID_ARGUMENT = '--server-session-id';

/** Exit property keys; mirror `ServerLifecycleTelemetry.ExitCodeKey` / `UptimeMsKey` (plus `ExitSignal`, POSIX only). */
export const ExitProperties = {
  exitCode: 'ExitCode',
  exitSignal: 'ExitSignal',
  uptimeMs: 'UptimeMs',
} as const;

type Properties = Record<string, string | number>;

interface Run {
  sessionId: string;
  startedAt: number;
  collector: CrashReportCollector;
  exit?: { code: number | null; signal: NodeJS.Signals | null; at: number };
  closed: Promise<void>;
  markClosed: () => void;
}

/**
 * Follows the language-server process across restarts for the server-lifecycle telemetry: gives each start
 * its own `--server-session-id` (the server stamps it as `SessionId`, so an exit joins to the dead run's
 * events), and records how the process ended and the crash report the .NET runtime printed to its stderr.
 *
 * The server is still launched by vscode-languageclient from the `args` array passed in here, which keeps the
 * client's own process handling (its kill-if-still-alive check on stop): {@link onStarting} rewrites the id in
 * that array, and runs synchronously from the client's `Starting` state change, before it spawns the process.
 */
export class ServerProcessTracker {
  private _run: Run | undefined;

  constructor(
    private readonly _args: string[],
    private readonly _now: () => number = Date.now,
    private readonly _newId: () => string = randomUUID,
  ) {}

  /** The current start's id, or `undefined` before the first start. */
  get sessionId(): string | undefined {
    return this._run?.sessionId;
  }

  /** A new start is about to spawn the server: give it a fresh id. */
  onStarting(): void {
    const sessionId = this._newId();
    const index = this._args.indexOf(SERVER_SESSION_ID_ARGUMENT);
    if (index >= 0 && index + 1 < this._args.length) {
      this._args[index + 1] = sessionId;
    } else {
      this._args.push(SERVER_SESSION_ID_ARGUMENT, sessionId);
    }

    let markClosed: () => void = () => undefined;
    const closed = new Promise<void>((resolve) => (markClosed = resolve));
    this._run = {
      sessionId,
      startedAt: this._now(),
      collector: new CrashReportCollector(),
      closed,
      markClosed,
    };
  }

  /** The client reached `Running`: watch its process (the client forgets it as soon as the connection closes). */
  onRunning(process: ChildProcess | undefined): void {
    const run = this._run;
    if (!run || !process) return;

    process.once('exit', (code, signal) => {
      run.exit = { code, signal, at: this._now() };
    });
    // 'close' follows 'exit' once the stdio streams have ended, so all of stderr has been read by then.
    process.once('close', () => run.markClosed());
    if (process.stderr) {
      readline
        .createInterface({
          input: process.stderr,
          crlfDelay: Infinity,
          terminal: false,
          historySize: 0,
        })
        .on('line', (line) => run.collector.onLine(line));
    }
  }

  /** `ServerSessionId` only: for a failed start, where there is no process to describe yet. */
  startDetails(): Properties {
    return this._run ? { [SERVER_SESSION_ID_PROPERTY]: this._run.sessionId } : {};
  }

  /**
   * The current run's exit details: `ServerSessionId`, `ExitCode` (signed, as Windows codes are reported by
   * Visual Studio) or `ExitSignal`, `UptimeMs`, and the parsed crash report. Waits up to `timeoutMs` for the
   * process to finish closing, because the client can notice the dead connection before the process has exited
   * and before the last of stderr (the crash report) has been read.
   */
  async describeExit(timeoutMs: number): Promise<Properties> {
    const run = this._run;
    if (!run) return {};

    let timer: NodeJS.Timeout | undefined;
    await Promise.race([
      run.closed,
      new Promise<void>((resolve) => (timer = setTimeout(resolve, timeoutMs))),
    ]);
    if (timer) clearTimeout(timer);

    const details: Properties = { [SERVER_SESSION_ID_PROPERTY]: run.sessionId };
    if (run.exit) {
      if (run.exit.code !== null) details[ExitProperties.exitCode] = run.exit.code | 0;
      if (run.exit.signal) details[ExitProperties.exitSignal] = run.exit.signal;
    }
    const uptime = (run.exit?.at ?? this._now()) - run.startedAt;
    if (uptime >= 0) details[ExitProperties.uptimeMs] = Math.round(uptime);

    const report = run.collector.parse();
    return report ? { ...details, ...crashReportProperties(report) } : details;
  }
}
