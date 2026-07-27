import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * A VS Code LogOutputChannel that simultaneously writes LSP trace messages to
 * the VS Code Output panel and to a timestamped log file in lsp-viewer format.
 *
 * Why LogOutputChannel (not plain OutputChannel):
 *   vscode-languageclient v10 types traceOutputChannel as LogOutputChannel and
 *   reads its logLevel property to decide whether tracing is enabled at all
 *   (see refreshTrace in its client.js): logLevel !== Trace collapses straight
 *   to Trace.Off, bypassing the `reqnroll.trace.server` setting entirely;
 *   anything else lets that setting pick Messages/Verbose. logLevel must
 *   therefore mirror the same setting `createTraceChannel` used to decide
 *   whether to allocate this channel in the first place — hardcoding it to
 *   Trace (as this class used to) meant vscode-languageclient always sent at
 *   least `trace: "messages"` in InitializeParams and via $/setTrace on
 *   config changes, even when the user had chosen "off".
 *
 * Why only trace() writes to file:
 *   vscode-languageclient routes all LSP request/response/notification entries
 *   through channel.trace().  debug()/info()/warn()/error() carry general client
 *   diagnostics (connection state, errors) but not the per-message trace lines.
 *   The lsp-viewer only needs the per-message entries, so those are the only ones
 *   tee-d to the file.
 *
 * File format:
 *   Each entry is written as a single line:
 *     [LSP   - HH:mm:ss] {"isLSPMessage":true,"type":"...","message":{...},"timestamp":ms}
 *   This matches the format produced by the VS extension's LspInspectorLogger and
 *   is the format expected by https://lampepfl.github.io/lsp-viewer/.
 *   The message arriving at trace() is human-readable text (TraceFormat.Text),
 *   so we parse it and reconstruct the JSON-RPC envelope ourselves.
 *
 * File path convention:
 *   Windows : %LOCALAPPDATA%\Reqnroll\reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log
 *   macOS   : ~/Library/Logs/Reqnroll/reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log
 *   Linux   : ~/.local/share/Reqnroll/reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log
 */
class TeeLogOutputChannel implements vscode.LogOutputChannel {
  readonly name: string;
  private readonly _inner: vscode.LogOutputChannel;
  private _stream: fs.WriteStream | undefined;
  private readonly _onDidChangeLogLevel = new vscode.EventEmitter<vscode.LogLevel>();
  private readonly _configListener: vscode.Disposable;

  constructor(name: string, stream: fs.WriteStream | undefined) {
    this._inner = vscode.window.createOutputChannel(name, { log: true });
    this.name = this._inner.name;
    this._stream = stream;
    // vscode-languageclient's own onDidChangeConfiguration listener calls
    // refreshTrace(), but that reads a *cached* copy of logLevel that only
    // updates via onDidChangeLogLevel — so this channel must fire its own
    // event on the same setting change for a live "off" toggle (no window
    // reload) to actually reach the server as `$/setTrace`.
    this._configListener = vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('reqnroll.trace.server')) {
        this._onDidChangeLogLevel.fire(this.logLevel);
      }
    });
  }

  get logLevel(): vscode.LogLevel {
    const level = vscode.workspace.getConfiguration('reqnroll').get<string>('trace.server', 'off');
    return level === 'off' ? vscode.LogLevel.Off : vscode.LogLevel.Trace;
  }

  get onDidChangeLogLevel(): vscode.Event<vscode.LogLevel> {
    return this._onDidChangeLogLevel.event;
  }

  trace(message: string, ...args: unknown[]): void {
    this._inner.trace(message, ...args);
    this._writeLspEntry(message);
  }

  // General client diagnostics — forward to panel only, not to the trace file.
  debug(message: string, ...args: unknown[]): void {
    this._inner.debug(message, ...args);
  }
  info(message: string, ...args: unknown[]): void {
    this._inner.info(message, ...args);
  }
  warn(message: string, ...args: unknown[]): void {
    this._inner.warn(message, ...args);
  }
  error(message: string | Error, ...args: unknown[]): void {
    this._inner.error(message, ...args);
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
  show(_colOrFocus?: vscode.ViewColumn | boolean, _focus?: boolean): void {
    this._inner.show();
  }

  hide(): void {
    this._inner.hide();
  }

  dispose(): void {
    this._inner.dispose();
    this._stream?.end();
    this._stream = undefined;
    this._configListener.dispose();
    this._onDidChangeLogLevel.dispose();
  }

  private _writeLspEntry(message: string): void {
    if (!this._stream) return;
    const entry = parseLspTraceMessage(message);
    if (!entry) return;
    // 3 spaces between LSP and - matches VS extension LspInspectorLogger format
    const now = new Date();
    const hh = String(now.getHours()).padStart(2, '0');
    const mm = String(now.getMinutes()).padStart(2, '0');
    const ss = String(now.getSeconds()).padStart(2, '0');
    this._stream.write(`[LSP   - ${hh}:${mm}:${ss}] ${JSON.stringify(entry)}\n`);
  }
}

type LspMessageType =
  | 'send-request'
  | 'receive-request'
  | 'send-response'
  | 'receive-response'
  | 'send-notification'
  | 'receive-notification';

interface LspEntry {
  isLSPMessage: true;
  type: LspMessageType;
  message: Record<string, unknown>;
  timestamp: number;
}

type ParsedHead = { type: LspMessageType; method?: string; id?: string };

/**
 * Patterns for the summary line vscode-jsonrpc produces under TraceFormat.Text, tried in order
 * and stopping at the first match. Order matters: "response" patterns are checked before the
 * catch-all "request" patterns.
 */
const HEAD_PATTERNS: { regex: RegExp; parse: (m: RegExpMatchArray) => ParsedHead }[] = [
  {
    regex: /^Sending request '(.+?) - \((.+?)\)'\./,
    parse: (m) => ({ type: 'send-request', method: m[1], id: m[2] }),
  },
  {
    regex: /^Received request '(.+?) - \((.+?)\)'\./,
    parse: (m) => ({ type: 'receive-request', method: m[1], id: m[2] }),
  },
  {
    regex: /^Sending response '(.+?) - \((.+?)\)'\./,
    parse: (m) => ({ type: 'send-response', method: m[1], id: m[2] }),
  },
  {
    regex: /^Received response '(.+?) - \((.+?)\)' in \d+ms\./,
    parse: (m) => ({ type: 'receive-response', method: m[1], id: m[2] }),
  },
  {
    // "without active response promise" variant — no method available
    regex: /^Received response (\S+) without active response promise\./,
    parse: (m) => ({ type: 'receive-response', id: m[1] }),
  },
  {
    regex: /^Sending notification '(.+?)'\./,
    parse: (m) => ({ type: 'send-notification', method: m[1] }),
  },
  {
    regex: /^Received notification '(.+?)'\./,
    parse: (m) => ({ type: 'receive-notification', method: m[1] }),
  },
];

/**
 * Parses the human-readable text that vscode-jsonrpc produces under TraceFormat.Text
 * and rebuilds the {"isLSPMessage":true,...} JSON object the lsp-viewer expects.
 *
 * vscode-jsonrpc passes two strings to the tracer: a summary line ("Sending request
 * 'method - (id)'.") and optionally a body ("Params: {...}").  vscode-languageclient
 * joins them with \n before calling channel.trace(), so we receive the combined text.
 */
function parseLspTraceMessage(text: string): LspEntry | undefined {
  const nl = text.indexOf('\n');
  const firstLine = nl >= 0 ? text.slice(0, nl) : text;
  const bodyStr = nl >= 0 ? text.slice(nl + 1) : '';

  let head: ParsedHead | undefined;
  for (const { regex, parse } of HEAD_PATTERNS) {
    const m = firstLine.match(regex);
    if (m) {
      head = parse(m);
      break;
    }
  }

  if (!head) return undefined;

  const isResponse = head.type === 'send-response' || head.type === 'receive-response';

  const rpcMsg: Record<string, unknown> = { jsonrpc: '2.0' };
  // Responses must NOT carry "method" — the viewer correlates response→request by id.
  // Requests and notifications always carry "method".
  if (!isResponse && head.method !== undefined) rpcMsg['method'] = head.method;
  if (head.id !== undefined) {
    const n = Number(head.id);
    rpcMsg['id'] = isNaN(n) ? head.id : n;
  }

  let hasResultOrError = false;
  if (bodyStr) {
    // Body is "Params: <json>", "Result: <json>", or "Error data: <json>"
    const bm = bodyStr.match(/^(Params|Result|Error data): ([\s\S]+)$/);
    if (bm) {
      try {
        const json = JSON.parse(bm[2]) as unknown;
        if (bm[1] === 'Params') rpcMsg['params'] = json;
        else if (bm[1] === 'Result') {
          rpcMsg['result'] = json;
          hasResultOrError = true;
        } else {
          rpcMsg['error'] = { data: json };
          hasResultOrError = true;
        }
      } catch {
        // Malformed JSON — omit the field rather than crashing
      }
    }
  }

  // Responses with no parsed result/error get result:null — matches VS extension format
  // where the raw JSON-RPC response always carries an explicit "result" field.
  if (isResponse && !hasResultOrError) rpcMsg['result'] = null;

  return { isLSPMessage: true, type: head.type, message: rpcMsg, timestamp: Date.now() };
}

/**
 * Maps the `reqnroll.trace.server` setting onto the LSP server's `--log-level` argument, so the
 * one lever VS Code users already have also controls the server's own file/protocol log
 * verbosity instead of only the client-side wire trace.
 */
export function traceServerToLogLevel(): 'Warning' | 'Info' | 'Verbose' {
  const level = vscode.workspace.getConfiguration('reqnroll').get<string>('trace.server', 'off');
  switch (level) {
    case 'verbose':
      return 'Verbose';
    case 'messages':
      return 'Info';
    default:
      return 'Warning';
  }
}

/**
 * Creates a trace output channel whose verbosity is controlled by the
 * `reqnroll.trace.server` VS Code setting (`off` | `messages` | `verbose`).
 *
 * When the setting is not `off`, a log file is opened in the Reqnroll log
 * directory so that every JSON-RPC message captured by vscode-languageclient
 * is persisted in lsp-viewer format alongside the VS Code Output panel entry.
 */
export function createTraceChannel(): vscode.LogOutputChannel {
  const level = vscode.workspace.getConfiguration('reqnroll').get<string>('trace.server', 'off');

  if (level === 'off') {
    return vscode.window.createOutputChannel('Reqnroll LSP Trace', { log: true });
  }

  let stream: fs.WriteStream | undefined;
  try {
    const logDir = resolveLogDirectory();
    fs.mkdirSync(logDir, { recursive: true });
    const n = new Date();
    const ts = `${n.getFullYear()}${String(n.getMonth() + 1).padStart(2, '0')}${String(n.getDate()).padStart(2, '0')}-${String(n.getHours()).padStart(2, '0')}${String(n.getMinutes()).padStart(2, '0')}${String(n.getSeconds()).padStart(2, '0')}`;
    const logPath = path.join(logDir, `reqnroll-vscode-inspector-${ts}.log`);
    stream = fs.createWriteStream(logPath, { flags: 'a' });
  } catch {
    // File logging unavailable; the VS Code output channel is the fallback.
  }

  return new TeeLogOutputChannel('Reqnroll LSP Trace', stream);
}

function resolveLogDirectory(): string {
  switch (process.platform) {
    case 'win32':
      return path.join(process.env['LOCALAPPDATA'] ?? os.homedir(), 'Reqnroll');
    case 'darwin':
      return path.join(os.homedir(), 'Library', 'Logs', 'Reqnroll');
    default:
      return path.join(os.homedir(), '.local', 'share', 'Reqnroll');
  }
}
