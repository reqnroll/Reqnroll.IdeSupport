import type * as vscode from 'vscode';
import type { ErrorHandler, ErrorHandlerResult, Message } from 'vscode-languageclient';
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
 * exception type name, a path-scrubbed and length-capped message, and a closed `Source` are sent.
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
const MAX_MESSAGE_LENGTH = 512;
const MAX_TYPE_LENGTH = 64;
/** Most `UnhandledException` events one extension session may send, however many distinct errors occur. */
export const MAX_EVENTS_PER_SESSION = 20;
/** Bound on remembered dedupe keys, so a stream of unique messages cannot grow memory without limit. */
const MAX_REMEMBERED_KEYS = 200;

// Windows absolute/UNC paths and POSIX absolute paths; deliberately broad (over-redacting is safe).
// Mirrors TelemetryScrubber.PathPattern in the LSP server (issue #843).
const PATH_PATTERN = /(?:[A-Za-z]:\\|\\\\|\/)[^\s"'<>:*?|]+/g;

/** Replaces filesystem-path-shaped substrings with `<path>`. Exported for tests. */
export function redactPaths(text: string): string {
  return text.replace(PATH_PATTERN, '<path>');
}

/** The `Source` for an exception thrown by the command registered under `commandId` (a static id from this extension). */
export function commandSource(commandId: string): string {
  return `${COMMAND_SOURCE_PREFIX}${commandId}`;
}

/** Builds the `UnhandledException` properties for `error`; pure, exported for tests. */
export function buildExceptionProperties(source: string, error: unknown): Record<string, string> {
  const type = exceptionTypeName(error);
  const rawMessage = error instanceof Error ? error.message : safeToString(error);
  let message = redactPaths(rawMessage ?? '');
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
 * Sends each distinct (source, type, scrubbed message) at most once per session and no more than
 * {@link MAX_EVENTS_PER_SESSION} events in total. Never throws.
 */
export class ClientExceptionReporter {
  private readonly _seen = new Set<string>();
  private _sent = 0;

  constructor(private readonly _send: SendException) {}

  /** @returns true if an event was handed to the sender. */
  report(source: string, error: unknown): boolean {
    try {
      if (this._sent >= MAX_EVENTS_PER_SESSION || isCancellation(error)) return false;
      const properties = buildExceptionProperties(source, error);
      const key = `${properties.Source}|${properties.ExceptionType}|${properties.Message}`;
      if (this._seen.has(key)) return false;
      if (this._seen.size >= MAX_REMEMBERED_KEYS) return false;
      this._seen.add(key);
      this._sent += 1;
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
