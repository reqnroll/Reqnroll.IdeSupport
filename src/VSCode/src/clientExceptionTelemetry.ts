import * as vscode from 'vscode';
import {
  ResponseError,
  type ErrorHandler,
  type ErrorHandlerResult,
  type Message,
} from 'vscode-languageclient';
import { sendTelemetryEvent } from './telemetry';
import { TelemetryEvents } from './telemetryEvents';

/**
 * Client-side exception telemetry (issue #621): reports exceptions thrown in the VS Code
 * extension's *own* code (activation, command handlers, the language client's transport error
 * callback) as the same `UnhandledException` event the LSP server emits for its exceptions
 * (`LspErrorTelemetryService.MonitorError`), so error-rate queries stay uniform. Client-reported
 * events are told apart from server ones by `ExceptionOrigin = "Client"` (absent on server events);
 * `IdeClient` (stamped by `sendTelemetryEvent`) says which IDE.
 *
 * Privacy contract (same bar as the server's `UnhandledException`, issues #620/#843): only the
 * exception type name, a scrubbed (quoted text, URLs and paths removed) and 128-character-capped message, and a closed `Source` are sent.
 * No stack trace is ever sent (issue #620 is open; assume not).
 *
 * Reporting must never throw into, or slow, the code it observes: every entry point swallows its own
 * failures, and a per-session dedupe + cap bounds volume.
 */

/** Value of the `ExceptionOrigin` property on every event this module sends. */
export const EXCEPTION_ORIGIN_CLIENT = 'Client';

/** Closed `Source` for an exception that escapes `activate`. */
export const SOURCE_ACTIVATION = 'Activation';
/** Closed `Source` for the language client's transport error callback (message read/write failures). */
export const SOURCE_LANGUAGE_CLIENT_ERROR_HANDLER = 'LanguageClientErrorHandler';
const COMMAND_SOURCE_PREFIX = 'Command:';

const CANCELLATION_NAMES = new Set(['Canceled', 'CancellationError', 'AbortError']);
// Exception text from the client can be arbitrary (Node/VS Code/JSON.parse errors quote file names, user
// directories, URLs with tokens and source snippets), so the scrub is stricter than the server's and the
// message is capped hard. A JSON.parse-style message embeds a snippet of the parsed text in quotes: every
// quoted substring is dropped and the cap is 128 characters, which also bounds anything the rules miss.
const MAX_MESSAGE_LENGTH = 128;
const MAX_TYPE_LENGTH = 64;
/** Most `UnhandledException` events one extension session may send, however many distinct errors occur. */
export const MAX_EVENTS_PER_SESSION = 20;

// Source/config file extensions whose bare names (or relative paths ending in them) are redacted.
const FILE_EXTENSIONS =
  'feature|cs|csproj|fsproj|vbproj|sln|slnx|slnf|json|md|xml|config|props|targets|txt|yml|yaml|ts|js|kt|java|ps1|sh|dll|exe|log|trx|runsettings|vb|fs|razor|cshtml|proj|projitems|shproj|nuspec|resx|ini|toml|lock';
// Directory segments may contain interior spaces ("C:\Users\John Smith\x") but never start or end with one,
// so a path cannot swallow the words (or a second path) that follow it.
const SEG = String.raw`(?:[^\\/\s"'<>|*?:](?:[^\\/\r\n"'<>|*?:]*[^\\/\s"'<>|*?:])?[\\/])*`;
const LAST = String.raw`[^\\/\s"'<>|*?:]*`;
// Order matters: quoted text first, then URLs, then path shapes from most to least specific.
// Double quotes: from the first to the LAST quote on the line (V8 embeds the JSON snippet unescaped, so
// nested quotes must not end the span early); an unbalanced quote drops the rest of the line.
const QUOTED = /"[^\r\n]*"|"[^\r\n]*|`[^`\r\n]*`|'[^'\r\n]{2,}'/g;
const URL_PATTERN = /\b[A-Za-z][A-Za-z0-9+.-]*:\/\/[^\s"'<>]+/g;
const PATH_PATTERNS: RegExp[] = [
  new RegExp(String.raw`[A-Za-z]:[\\/]${SEG}${LAST}`, 'g'), // drive-letter
  new RegExp(String.raw`\\\\${SEG}${LAST}`, 'g'), // UNC
  new RegExp(String.raw`(?:~|%[A-Za-z_]\w*%|\$\{?[A-Za-z_]\w*\}?)[\\/]${SEG}${LAST}`, 'g'), // ~, %VAR%, $VAR
  new RegExp(String.raw`(?<![\w~%$}.])/(?=[^\s/])${SEG}${LAST}`, 'g'), // POSIX absolute
  /(?<![\w.])\.{1,2}[\\/][^\s"'<>|*?:]*/g, // ./x, ../x
  new RegExp(String.raw`(?<!\w)(?:[\w.-]+[\\/])*[\w.-]+\.(?:${FILE_EXTENSIONS})\b`, 'g'), // bare names / relative paths with a known extension
];

/**
 * Redacts everything path- or content-shaped from exception text: quoted substrings (`<text>`), URLs
 * (`<url>`, query string and fragment included), and absolute, UNC, home/env-var, relative and
 * bare-file-name paths (`<path>`). Exported for tests.
 */
export function scrubMessage(text: string): string {
  let result = text.replace(QUOTED, '<text>').replace(URL_PATTERN, '<url>');
  for (const pattern of PATH_PATTERNS) result = result.replace(pattern, '<path>');
  return result;
}

/** The `Source` for an exception thrown by the command registered under `commandId` (a static id from this extension). */
export function commandSource(commandId: string): string {
  return `${COMMAND_SOURCE_PREFIX}${commandId}`;
}

/** Builds the `UnhandledException` properties for `error`; pure, exported for tests. */
export function buildExceptionProperties(source: string, error: unknown): Record<string, string> {
  const type = exceptionTypeName(error);
  const rawMessage = error instanceof Error ? error.message : safeToString(error);
  // Cap before and after scrubbing: the raw text is bounded first so a huge message cannot make the scrub slow.
  let message = scrubMessage((rawMessage ?? '').slice(0, 4 * MAX_MESSAGE_LENGTH));
  if (message.length > MAX_MESSAGE_LENGTH) message = message.slice(0, MAX_MESSAGE_LENGTH);
  return {
    ExceptionType: type,
    Message: message,
    Source: source,
    ExceptionOrigin: EXCEPTION_ORIGIN_CLIENT,
  };
}

/**
 * Exception type name: the JS error `name` (or its constructor name for a plain `Error`-named
 * subclass) when it is a safe identifier, else `Error`; `NonError` for a thrown non-Error value.
 */
function exceptionTypeName(error: unknown): string {
  if (!(error instanceof Error)) return 'NonError';
  const candidates = [error.name, error.constructor?.name];
  for (const candidate of candidates) {
    if (
      typeof candidate === 'string' &&
      candidate !== 'Error' &&
      candidate.length <= MAX_TYPE_LENGTH &&
      /^[A-Za-z_$][\w$]*$/.test(candidate)
    ) {
      return candidate;
    }
  }
  return 'Error';
}

/** Cancellation is control flow (a user dismissing a picker, a cancelled request), not a failure. */
function isCancellation(error: unknown): boolean {
  return error instanceof Error && CANCELLATION_NAMES.has(error.name);
}

function safeToString(value: unknown): string {
  try {
    return String(value);
  } catch {
    return '';
  }
}

export type SendException = (eventName: string, properties: Record<string, string>) => void;

/**
 * Errors that are not ours to report: an LSP `ResponseError` (the server already reports its own failures,
 * and the text can embed step or expression text), and errors raised by a VS Code built-in command we merely
 * delegate to (see {@link executeForeignCommand}).
 */
const foreignErrors = new WeakSet<object>();

function isResponseError(error: unknown): boolean {
  if (!(error instanceof Error)) return false;
  if (error instanceof ResponseError) return true;
  const cause = (error as { cause?: unknown }).cause;
  return (
    error.constructor?.name === 'ResponseError' || (cause !== undefined && isResponseError(cause))
  );
}

function isForeign(error: unknown): boolean {
  return typeof error === 'object' && error !== null && foreignErrors.has(error);
}

/**
 * Runs a VS Code built-in command this extension merely delegates to (e.g. the F2 fall-through to
 * `editor.action.rename`). A failure there is VS Code's or another extension's, not ours: it is rethrown
 * unchanged but never reported.
 */
export async function executeForeignCommand(
  command: string,
  execute: (command: string) => Thenable<unknown> = (c) => vscode.commands.executeCommand(c),
): Promise<void> {
  try {
    await execute(command);
  } catch (err) {
    if (typeof err === 'object' && err !== null) foreignErrors.add(err);
    throw err;
  }
}

/**
 * Sends each distinct (source, type, scrubbed message) at most once per session and no more than
 * {@link MAX_EVENTS_PER_SESSION} events in total. Events dropped as duplicates or by the cap are counted and
 * the count rides on the next event that is sent as `SuppressedCount` (events dropped after the cap is
 * reached are therefore never visible). Never throws.
 */
export class ClientExceptionReporter {
  private readonly _seen = new Set<string>();
  private _sent = 0;
  private _suppressed = 0;

  constructor(private readonly _send: SendException) {}

  /** @returns true if an event was handed to the sender. */
  report(source: string, error: unknown): boolean {
    try {
      if (isCancellation(error) || isResponseError(error) || isForeign(error)) return false;
      const properties = buildExceptionProperties(source, error);
      const key = `${properties.Source}|${properties.ExceptionType}|${properties.Message}`;
      if (this._sent >= MAX_EVENTS_PER_SESSION || this._seen.has(key)) {
        this._suppressed += 1;
        return false;
      }
      this._seen.add(key);
      this._sent += 1;
      if (this._suppressed > 0) {
        properties.SuppressedCount = String(this._suppressed);
        this._suppressed = 0;
      }
      this._send(TelemetryEvents.unhandledException, properties);
      return true;
    } catch {
      // Telemetry must never break the host code path it observes.
      return false;
    }
  }
}

// Session-wide instance. `sendTelemetryEvent` no-ops until the shared reporter exists and honours
// REQNROLL_TELEMETRY_ENABLED and VS Code's own telemetry opt-out.
const sessionReporter = new ClientExceptionReporter((name, props) =>
  sendTelemetryEvent(name, props),
);

/** Reports `error` as a client-side `UnhandledException` attributed to `source`. Never throws. */
export function reportClientException(source: string, error: unknown): void {
  sessionReporter.report(source, error);
}

/**
 * Wraps a command handler so an exception it throws (or rejects with) is reported, then rethrown
 * unchanged: VS Code's own command-error handling and the user-visible behaviour stay as before.
 */
export function guardCommand<A extends unknown[], R>(
  commandId: string,
  handler: (...args: A) => R,
  report: (source: string, error: unknown) => void = reportClientException,
): (...args: A) => R {
  const source = commandSource(commandId);
  return (...args: A): R => {
    try {
      const result = handler(...args);
      if (result instanceof Promise) {
        return result.catch((err: unknown) => {
          report(source, err);
          throw err;
        }) as R;
      }
      return result;
    } catch (err) {
      report(source, err);
      throw err;
    }
  };
}

/** Registers `handler` under `commandId` with {@link guardCommand} applied. */
export function registerGuardedCommand<A extends unknown[], R>(
  register: typeof vscode.commands.registerCommand,
  commandId: string,
  handler: (...args: A) => R,
): vscode.Disposable {
  return register(commandId, guardCommand(commandId, handler));
}

/**
 * Wraps the language client's error handler so transport errors (a failed message read/write) are
 * reported, while the decision of whether to continue/shut down stays with `inner` (the client's
 * default handler). `closed()` is left untouched: a closed connection is server-lifecycle, covered
 * by the server-lifecycle events (#845), not a client exception.
 */
export function wrapErrorHandler(
  inner: ErrorHandler,
  report: (source: string, error: unknown) => void = reportClientException,
): ErrorHandler {
  return {
    error(
      error: Error,
      message: Message | undefined,
      count: number | undefined,
    ): ErrorHandlerResult | Promise<ErrorHandlerResult> {
      report(SOURCE_LANGUAGE_CLIENT_ERROR_HANDLER, error);
      return inner.error(error, message, count);
    },
    closed: () => inner.closed(),
  };
}

/**
 * An error handler for `LanguageClientOptions.errorHandler` that reports transport errors via
 * {@link wrapErrorHandler} around the client's own default handler. `getDefault` is called lazily
 * (on first use) because the client does not exist yet when its options are built.
 */
export function createReportingErrorHandler(getDefault: () => ErrorHandler): ErrorHandler {
  let wrapped: ErrorHandler | undefined;
  const handler = () => (wrapped ??= wrapErrorHandler(getDefault()));
  return {
    error: (error, message, count) => handler().error(error, message, count),
    closed: () => handler().closed(),
  };
}
