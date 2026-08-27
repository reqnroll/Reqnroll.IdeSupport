import * as vscode from 'vscode';
import { CodeLensRefreshRequest, LanguageClient } from 'vscode-languageclient/node';

let sharedEmitter: vscode.EventEmitter<void> | undefined;

/**
 * Returns a shared `onDidChangeCodeLenses` event, backed by exactly one
 * `workspace/codeLens/refresh` `onRequest` registration.
 *
 * `MessageConnection.onRequest` silently replaces any earlier handler registered for the same
 * method (a plain `Map.set` under the hood — no error, no warning), so if each CodeLens provider
 * (step usages, hooks — issue #269) registered its own `onRequest(CodeLensRefreshRequest.type, ...)`,
 * whichever registered last would win and the other's refresh push would go silently dead. Every
 * provider that wants to react to the server's refresh push must share this one registration
 * instead.
 */
export function getCodeLensRefreshEvent(
  client: LanguageClient,
  context: vscode.ExtensionContext,
): vscode.Event<void> {
  if (!sharedEmitter) {
    sharedEmitter = new vscode.EventEmitter<void>();
    context.subscriptions.push(
      sharedEmitter,
      client.onRequest(CodeLensRefreshRequest.type, () => {
        sharedEmitter!.fire();
      }),
    );
  }
  return sharedEmitter.event;
}
