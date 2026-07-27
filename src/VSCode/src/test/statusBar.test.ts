import * as assert from 'assert';
import { LanguageClient } from 'vscode-languageclient/node';
import { StatusBarManager } from '../statusBar';

/** Minimal stand-in for LanguageClient's onDidChangeState surface used by StatusBarManager. */
function fakeClient(onListenerDisposed: () => void): LanguageClient {
  return {
    onDidChangeState: () => ({ dispose: onListenerDisposed }),
  } as unknown as LanguageClient;
}

suite('StatusBarManager', () => {
  test('dispose() also disposes the onDidChangeState listener (issue #325)', () => {
    // Without disposing the listener, a StatusBarManager constructed again against a
    // longer-lived client (any future reconnect-without-restart path) would add another
    // permanent listener to that client for every prior instance's dispose().
    let listenerDisposed = false;
    const client = fakeClient(() => {
      listenerDisposed = true;
    });
    const manager = new StatusBarManager(client);

    manager.dispose();

    assert.strictEqual(listenerDisposed, true);
  });
});
