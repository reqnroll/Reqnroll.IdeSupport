import * as assert from 'assert';
import { State } from 'vscode-languageclient';
import { LanguageClient, StateChangeEvent } from 'vscode-languageclient/node';
import { onServerRestarted } from '../../lsp/serverRestart';

function setup(initial: State): {
  restarts: () => number;
  move: (to: State) => void;
  dispose: () => void;
} {
  let state = initial;
  let listener: ((e: StateChangeEvent) => void) | undefined;
  const client = {
    get state() {
      return state;
    },
    onDidChangeState: (l: (e: StateChangeEvent) => void) => {
      listener = l;
      return { dispose: () => (listener = undefined) };
    },
  } as unknown as Pick<LanguageClient, 'state' | 'onDidChangeState'>;
  let count = 0;
  const disposable = onServerRestarted(client, () => (count += 1));
  return {
    restarts: () => count,
    move: (to: State) => {
      const oldState = state;
      state = to;
      listener?.({ oldState, newState: to });
    },
    dispose: () => {
      disposable.dispose();
    },
  };
}

suite('onServerRestarted (issue #997)', () => {
  test('the first Running after subscribing before start is not a restart', () => {
    const { restarts, move } = setup(State.Stopped);
    move(State.Starting);
    move(State.Running);
    assert.strictEqual(restarts(), 0);
  });

  test('Running -> Stopped -> Starting -> Running fires once', () => {
    const { restarts, move } = setup(State.Stopped);
    move(State.Starting);
    move(State.Running);
    move(State.Stopped);
    move(State.Starting);
    move(State.Running);
    assert.strictEqual(restarts(), 1);
  });

  test('subscribing while already Running treats the next Running as a restart', () => {
    const { restarts, move } = setup(State.Running);
    move(State.Stopped);
    move(State.Starting);
    move(State.Running);
    assert.strictEqual(restarts(), 1);
  });

  test('a restart that fails before Running does not fire', () => {
    const { restarts, move } = setup(State.Running);
    move(State.Stopped);
    move(State.Starting);
    move(State.StartFailed);
    assert.strictEqual(restarts(), 0);
  });

  test('dispose() stops further notifications', () => {
    const { restarts, move, dispose } = setup(State.Running);
    dispose();
    move(State.Stopped);
    move(State.Starting);
    move(State.Running);
    assert.strictEqual(restarts(), 0);
  });
});
