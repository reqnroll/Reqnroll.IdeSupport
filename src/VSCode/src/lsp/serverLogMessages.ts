import * as vscode from 'vscode';
import { ReqnrollMethods } from './lspMethods';

/** The `reqnroll/logMessage` parameters: the same `{ type, message }` shape as `window/logMessage`. */
export interface ServerLogMessageParams {
  /** LSP `MessageType`: 1 Error, 2 Warning, 3 Info, 4 Log. */
  type?: number;
  message?: string;
}

/** The channel methods a server message is written with. */
export type ServerLogMessageChannel = Pick<
  vscode.LogOutputChannel,
  'error' | 'warn' | 'info' | 'debug'
>;

/** Subscribes to notifications by method name; satisfied by `LanguageClient`. */
export interface NotificationSource {
  onNotification(method: string, handler: (params: unknown) => void): vscode.Disposable;
}

/**
 * Writes one `reqnroll/logMessage` to `channel` at its level. Exported for tests.
 */
export function writeServerLogMessage(channel: ServerLogMessageChannel, params: unknown): void {
  const { type, message } = (params ?? {}) as ServerLogMessageParams;
  if (!message) return;

  switch (type) {
    case 1:
      channel.error(message);
      break;
    case 2:
      channel.warn(message);
      break;
    case 3:
      channel.info(message);
      break;
    default:
      channel.debug(message);
      break;
  }
}

/**
 * Writes the server's `reqnroll/logMessage` notifications (problems the user can act on, such as a
 * failed binding discovery) to the curated "Reqnroll" channel. The server sends these instead of
 * `window/logMessage`, which vscode-languageclient writes to the "Reqnroll LSP" channel among the
 * library's own diagnostics.
 */
export function registerServerLogMessages(
  client: NotificationSource,
  channel: ServerLogMessageChannel,
): vscode.Disposable {
  return client.onNotification(ReqnrollMethods.logMessage, (params: unknown) =>
    writeServerLogMessage(channel, params),
  );
}
