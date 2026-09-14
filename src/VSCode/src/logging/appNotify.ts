import * as vscode from 'vscode';

let appLogChannel: vscode.LogOutputChannel | undefined;

/**
 * Wires the curated "Reqnroll" app-status channel (issue #661) that `showInfo`/`showWarn`/
 * `showError` below mirror every popup notification into, alongside the extension/LSP-client
 * lifecycle lines `extension.ts` and `statusBar.ts` write directly. Set once by `extension.ts`
 * during `activate()`; left unset when a command module is exercised directly by a unit test, in
 * which case the mirroring below is simply skipped.
 */
export function setAppLogChannel(channel: vscode.LogOutputChannel | undefined): void {
  appLogChannel = channel;
}

/** Shows an information message popup and mirrors it to the curated "Reqnroll" output channel. */
export function showInfo(message: string, ...items: string[]): Thenable<string | undefined> {
  appLogChannel?.info(message);
  return vscode.window.showInformationMessage(message, ...items);
}

/** Shows a warning message popup and mirrors it to the curated "Reqnroll" output channel. */
export function showWarn(message: string, ...items: string[]): Thenable<string | undefined> {
  appLogChannel?.warn(message);
  return vscode.window.showWarningMessage(message, ...items);
}

/** Shows an error message popup and mirrors it to the curated "Reqnroll" output channel. */
export function showError(message: string, ...items: string[]): Thenable<string | undefined> {
  appLogChannel?.error(message);
  return vscode.window.showErrorMessage(message, ...items);
}
