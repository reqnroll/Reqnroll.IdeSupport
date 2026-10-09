import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { pruneOldLogs, resolveLogDirectory } from './logPaths';

const LEVEL_FIELD_WIDTH = 7; // width of "Warning" - the longest level name used across all sinks

/**
 * Renders one log line's preamble + message (excluding the trailing newline) — the portable
 * subset of `Reqnroll.IdeSupport.Common.Logging.LogLineFormatter.FormatPreamble` shared with the
 * .NET side and the Rider plugin's `ReqnrollDebugLogger.formatLine` (issue #626): UTC timestamp,
 * a level padded to a fixed width, then the message. No thread id (Node has none worth reporting)
 * and no source/caller segment (VS Code's LogOutputChannel API doesn't hand the call site to a
 * `debug()`/`info()`/`warn()`/`error()` call the way C#'s `[CallerFilePath]` does for free).
 */
export function formatLine(level: string, message: string): string {
  return `${new Date().toISOString()} [${level.padEnd(LEVEL_FIELD_WIDTH)}] ${message}`;
}

/**
 * A VS Code LogOutputChannel that tees debug/info/warn/error entries to a file in the shared
 * Reqnroll log directory, alongside the Output panel.
 *
 * Closes a real gap (issue #626): VS's SynchronousFileLogger and Rider's ReqnrollDebugLogger both
 * give client diagnostics a durable, general-purpose log file, but VS Code's own general
 * "Reqnroll LSP" output channel previously only reached the Output panel — gone the moment the
 * window closed, unlike its two sibling IDEs.
 *
 * trace() is deliberately NOT teed here — that's the LSP wire-message stream, already captured
 * separately by `lspInspectorLogger.ts`'s own file tee in lsp-viewer format.
 */
export class GeneralFileLogChannel implements vscode.LogOutputChannel {
  readonly name: string;
  private readonly _inner: vscode.LogOutputChannel;
  private _stream: fs.WriteStream | undefined;
  private readonly _autoShowOnWarnOrError: boolean;
  private _hasAutoShownOnWarnOrError = false;

  constructor(
    name: string,
    stream: fs.WriteStream | undefined,
    autoShowOnWarnOrError = false,
    // Test seam: lets unit tests supply a fake channel so the wrapper's show() forwarding and
    // auto-show bookkeeping can be asserted without a real (or alive) VS Code window.
    inner?: vscode.LogOutputChannel,
  ) {
    this._inner = inner ?? vscode.window.createOutputChannel(name, { log: true });
    this.name = this._inner.name;
    this._stream = stream;
    this._autoShowOnWarnOrError = autoShowOnWarnOrError;
  }

  get logLevel(): vscode.LogLevel {
    return this._inner.logLevel;
  }
  get onDidChangeLogLevel(): vscode.Event<vscode.LogLevel> {
    return this._inner.onDidChangeLogLevel;
  }

  trace(message: string, ...args: unknown[]): void {
    this._inner.trace(message, ...args);
  }
  debug(message: string, ...args: unknown[]): void {
    this._inner.debug(message, ...args);
    this._write('Verbose', message);
  }
  info(message: string, ...args: unknown[]): void {
    this._inner.info(message, ...args);
    this._write('Info', message);
  }
  warn(message: string, ...args: unknown[]): void {
    this._inner.warn(message, ...args);
    this._write('Warning', message);
    this._autoShowOnFirstWarnOrError();
  }
  error(message: string | Error, ...args: unknown[]): void {
    this._inner.error(message, ...args);
    this._write(
      'Error',
      message instanceof Error ? `${message.message}\n${message.stack ?? ''}` : message,
    );
    this._autoShowOnFirstWarnOrError();
  }

  append(value: string): void {
    this._inner.append(value);
  }
  appendLine(value: string): void {
    this._inner.appendLine(value);
  }
  replace(value: string): void {
    this._inner.replace(value);
  }
  clear(): void {
    this._inner.clear();
  }

  show(preserveFocus?: boolean): void;
  show(column?: vscode.ViewColumn, preserveFocus?: boolean): void;
  show(colOrFocus?: vscode.ViewColumn | boolean, focus?: boolean): void {
    // Forward the caller's intent instead of unconditionally focusing (issue #1000): a bare
    // show() (the `reqnroll.showOutputChannel` command) focuses, while show(true) — used for
    // auto-reveal on warn/error — reveals the panel without stealing focus.
    if (typeof colOrFocus === 'boolean') {
      this._inner.show(colOrFocus);
    } else {
      this._inner.show(colOrFocus, focus);
    }
  }

  hide(): void {
    this._inner.hide();
  }

  dispose(): void {
    this._inner.dispose();
    this._stream?.end();
    this._stream = undefined;
  }

  /**
   * Reveals the panel on the *first* warn/error of the session, then stays quiet (issue #1000) —
   * matching the documented behaviour and the VS side's `VsOutputPaneLogger.ShouldActivate`. Uses
   * show(true) so the reveal preserves the editor focus the user is typing in.
   */
  private _autoShowOnFirstWarnOrError(): void {
    if (!this._autoShowOnWarnOrError || this._hasAutoShownOnWarnOrError) return;
    this._hasAutoShownOnWarnOrError = true;
    this.show(true);
  }

  private _write(level: string, message: string): void {
    if (!this._stream) return;
    try {
      this._stream.write(formatLine(level, message) + '\n');
    } catch {
      // Best-effort - file logging must never break the extension.
    }
  }
}

export interface GeneralLogChannelOptions {
  /** Distinguishes this channel's log file from other `createGeneralLogChannel` channels — see the file-path doc below. Defaults to `'ext'`. */
  filePrefix?: string;
  /**
   * Auto-reveals the Output panel on this channel's first warn()/error() call in a session
   * (issue #661), mirroring `VsOutputPaneLogger.ShouldActivate` on the VS side (PR #656/#651).
   * The reveal preserves focus (issue #1000) so it never interrupts typing.
   */
  autoShowOnWarnOrError?: boolean;
}

/**
 * Creates a general-purpose file-tee'd output channel, writing debug/info/warn/error to
 * `<Reqnroll log dir>/reqnroll-vscode-<filePrefix>-<yyyyMMdd>-<pid>.log` (pid distinguishes
 * concurrent VS Code windows, matching the VS/Rider file-naming convention) alongside the Output
 * panel. Used both for the general "Reqnroll LSP" channel (`filePrefix: 'ext'`, the default) and
 * the curated "Reqnroll" app-status channel (`filePrefix: 'app'`) — see `extension.ts`.
 */
export function createGeneralLogChannel(
  name: string,
  options?: GeneralLogChannelOptions,
): vscode.LogOutputChannel {
  const filePrefix = options?.filePrefix ?? 'ext';
  let stream: fs.WriteStream | undefined;
  try {
    const logDir = resolveLogDirectory();
    fs.mkdirSync(logDir, { recursive: true });
    pruneOldLogs(logDir);
    const date = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    const logPath = path.join(logDir, `reqnroll-vscode-${filePrefix}-${date}-${process.pid}.log`);
    stream = fs.createWriteStream(logPath, { flags: 'a' });
  } catch {
    // File logging unavailable; the Output panel is the fallback.
  }

  return new GeneralFileLogChannel(name, stream, options?.autoShowOnWarnOrError ?? false);
}
