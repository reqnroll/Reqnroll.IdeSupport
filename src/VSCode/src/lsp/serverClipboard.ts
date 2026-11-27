import * as vscode from 'vscode';
import { ReqnrollMethods } from './lspMethods';
import { NotificationSource } from './serverLogMessages';

/** The `reqnroll/copyToClipboard` parameters. */
export interface CopyToClipboardParams {
  text?: string;
}

/** The clipboard the text is written to; satisfied by `vscode.env.clipboard`. */
export type ClipboardTarget = Pick<vscode.Clipboard, 'writeText'>;

/** Where a failed write is reported; satisfied by the "Reqnroll" channel. */
export type ClipboardLog = Pick<vscode.LogOutputChannel, 'warn'>;

/**
 * Writes one `reqnroll/copyToClipboard` to `clipboard`. A notification without text is ignored.
 * Exported for tests.
 */
export async function copyServerTextToClipboard(
  clipboard: ClipboardTarget,
  log: ClipboardLog,
  params: unknown,
): Promise<void> {
  const { text } = (params ?? {}) as CopyToClipboardParams;
  if (typeof text !== 'string' || text.length === 0) return;

  try {
    await clipboard.writeText(text);
  } catch (err) {
    log.warn(`Could not copy the step definition to the clipboard: ${String(err)}`);
  }
}

/**
 * Puts the text of the server's `reqnroll/copyToClipboard` notifications on the clipboard. The
 * server sends one when the user picks a "Copy step definition(s) to clipboard" quick fix: LSP has
 * no clipboard API, so the server relays the text back for the client to copy.
 */
export function registerServerClipboard(
  client: NotificationSource,
  clipboard: ClipboardTarget,
  log: ClipboardLog,
): vscode.Disposable {
  return client.onNotification(ReqnrollMethods.copyToClipboard, (params: unknown) => {
    void copyServerTextToClipboard(clipboard, log, params);
  });
}
