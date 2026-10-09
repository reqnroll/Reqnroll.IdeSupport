import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import type { LanguageClient } from 'vscode-languageclient/node';

/**
 * Calls `listener` each time the language server comes back up after a restart, i.e. on every
 * transition to `Running` except the client's first one.
 *
 * vscode-languageclient's default error handler restarts a crashed server on the *same*
 * `LanguageClient` (10.x `handleConnectionClosed` -> `CloseAction.Restart` -> `start()`), which emits
 * `Running -> Stopped -> Starting -> Running` and re-sends `initialize`/`initialized`, but client-side
 * state built up by our own notifications is not replayed. Anything that pushed state to the old
 * server must push it again from here (issue #997).
 *
 * Safe to subscribe before or after the first start: a client already `Running` at subscription time
 * counts as having had its first run. `listener` fires synchronously inside the state change, before
 * `initialized` has been sent; `client.sendNotification` waits for the start to finish, so
 * notifications sent from it reach the new server after its `initialized`.
 */
export function onServerRestarted(
  client: Pick<LanguageClient, 'state' | 'onDidChangeState'>,
  listener: () => void,
): vscode.Disposable {
  let hasRun = client.state === State.Running;
  return client.onDidChangeState((event) => {
    if (event.newState !== State.Running) return;
    if (hasRun) listener();
    hasRun = true;
  });
}
