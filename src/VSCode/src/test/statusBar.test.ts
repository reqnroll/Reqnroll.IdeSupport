import * as assert from 'assert';
import { State } from 'vscode-languageclient';
import { LanguageClient, StateChangeEvent } from 'vscode-languageclient/node';
import { StatusBarManager } from '../statusBar';

/** Minimal stand-in for LanguageClient's onDidChangeState surface used by StatusBarManager. */
function fakeClient(overrides: {
  onListenerDisposed?: () => void;
  captureListener?: (listener: (event: StateChangeEvent) => void) => void;
}): LanguageClient {
  return {
    onDidChangeState: (listener: (event: StateChangeEvent) => void) => {
      overrides.captureListener?.(listener);
      return { dispose: overrides.onListenerDisposed ?? (() => {}) };
    },
  } as unknown as LanguageClient;
}

/** Reaches into StatusBarManager's private status bar item -- there's no public getter, and
 *  adding one purely for tests would widen the class's surface for no production benefit. */
function itemOf(manager: StatusBarManager): {
  text: string;
  tooltip?: string;
  backgroundColor?: unknown;
} {
  return (
    manager as unknown as { _item: { text: string; tooltip?: string; backgroundColor?: unknown } }
  )._item;
}

suite('StatusBarManager', () => {
  test('shows a starting indicator immediately on construction', () => {
    const manager = fakeManager();
    assert.match(itemOf(manager).text, /Reqnroll/);
    assert.strictEqual(itemOf(manager).tooltip, 'Reqnroll LSP server starting…');
    assert.strictEqual(itemOf(manager).backgroundColor, undefined);
  });

  test('reflects State.Running with no error background', () => {
    const { manager, fireStateChange } = fakeManagerWithListener();
    fireStateChange(State.Running);

    assert.strictEqual(itemOf(manager).tooltip, 'Reqnroll LSP server running');
    assert.strictEqual(itemOf(manager).backgroundColor, undefined);
  });

  test('reflects State.Stopped with an error background', () => {
    const { manager, fireStateChange } = fakeManagerWithListener();
    fireStateChange(State.Stopped);

    assert.strictEqual(itemOf(manager).tooltip, 'Reqnroll LSP server stopped');
    assert.notStrictEqual(itemOf(manager).backgroundColor, undefined);
  });

  test('reflects a later State.Starting (e.g. a reconnect) the same as the initial state', () => {
    const { manager, fireStateChange } = fakeManagerWithListener();
    fireStateChange(State.Running);

    fireStateChange(State.Starting);

    assert.strictEqual(itemOf(manager).tooltip, 'Reqnroll LSP server starting…');
    assert.strictEqual(itemOf(manager).backgroundColor, undefined);
  });

  test('dispose() also disposes the onDidChangeState listener (issue #325)', () => {
    // Without disposing the listener, a StatusBarManager constructed again against a
    // longer-lived client (any future reconnect-without-restart path) would add another
    // permanent listener to that client for every prior instance's dispose().
    let listenerDisposed = false;
    const client = fakeClient({ onListenerDisposed: () => (listenerDisposed = true) });
    const manager = new StatusBarManager(client);

    manager.dispose();

    assert.strictEqual(listenerDisposed, true);
  });
});

function fakeManager(): StatusBarManager {
  return new StatusBarManager(fakeClient({}));
}

function fakeManagerWithListener(): {
  manager: StatusBarManager;
  fireStateChange: (newState: State) => void;
} {
  let listener: ((event: StateChangeEvent) => void) | undefined;
  const manager = new StatusBarManager(fakeClient({ captureListener: (l) => (listener = l) }));
  return {
    manager,
    fireStateChange: (newState: State) => listener?.({ oldState: State.Starting, newState }),
  };
}
