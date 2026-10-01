import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  WALKTHROUGH_ID,
  WALKTHROUGH_SHOWN_KEY,
  showWalkthroughOnFirstActivation,
} from '../walkthrough';

function fakeContext(extensionMode: vscode.ExtensionMode, initial: Record<string, unknown> = {}) {
  const state = new Map<string, unknown>(Object.entries(initial));
  return {
    state,
    context: {
      extensionMode,
      globalState: {
        get: (key: string) => state.get(key),
        update: (key: string, value: unknown) => {
          state.set(key, value);
          return Promise.resolve();
        },
      },
    } as unknown as Pick<vscode.ExtensionContext, 'extensionMode' | 'globalState'>,
  };
}

suite('showWalkthroughOnFirstActivation', () => {
  test('opens the walkthrough once in production and records the flag', async () => {
    const { context, state } = fakeContext(vscode.ExtensionMode.Production);
    const calls: unknown[][] = [];
    const exec = (...args: unknown[]) => {
      calls.push(args);
      return Promise.resolve();
    };

    assert.strictEqual(await showWalkthroughOnFirstActivation(context, exec), true);
    assert.deepStrictEqual(calls, [['workbench.action.openWalkthrough', WALKTHROUGH_ID, false]]);
    assert.strictEqual(state.get(WALKTHROUGH_SHOWN_KEY), true);

    assert.strictEqual(await showWalkthroughOnFirstActivation(context, exec), false);
    assert.strictEqual(calls.length, 1);
  });

  test('does nothing when already shown', async () => {
    const { context } = fakeContext(vscode.ExtensionMode.Production, {
      [WALKTHROUGH_SHOWN_KEY]: true,
    });
    let called = false;
    assert.strictEqual(
      await showWalkthroughOnFirstActivation(context, () => {
        called = true;
        return Promise.resolve();
      }),
      false,
    );
    assert.strictEqual(called, false);
  });

  test('does nothing outside production mode', async () => {
    for (const mode of [vscode.ExtensionMode.Development, vscode.ExtensionMode.Test]) {
      const { context, state } = fakeContext(mode);
      let called = false;
      assert.strictEqual(
        await showWalkthroughOnFirstActivation(context, () => {
          called = true;
          return Promise.resolve();
        }),
        false,
      );
      assert.strictEqual(called, false);
      assert.strictEqual(state.has(WALKTHROUGH_SHOWN_KEY), false);
    }
  });
});
