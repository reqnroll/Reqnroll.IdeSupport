import * as assert from 'assert';
import * as vscode from 'vscode';
import { setAppLogChannel, showError, showInfo, showWarn } from '../../logging/appNotify';

/** Minimal stand-in for the subset of vscode.LogOutputChannel that appNotify.ts writes to. */
function fakeChannel(): {
  channel: vscode.LogOutputChannel;
  entries: { level: string; message: string }[];
} {
  const entries: { level: string; message: string }[] = [];
  const channel = {
    info: (message: string) => entries.push({ level: 'info', message }),
    warn: (message: string) => entries.push({ level: 'warn', message }),
    error: (message: string) => entries.push({ level: 'error', message }),
  } as unknown as vscode.LogOutputChannel;
  return { channel, entries };
}

interface WindowStubs {
  showInformationMessage?: (message: string, ...items: string[]) => Thenable<string | undefined>;
  showWarningMessage?: (message: string, ...items: string[]) => Thenable<string | undefined>;
  showErrorMessage?: (message: string, ...items: string[]) => Thenable<string | undefined>;
}

/** Stubs a vscode.window prompt function for the duration of `fn`, restoring it afterwards. */
async function withStubbedWindow<T>(overrides: WindowStubs, fn: () => Thenable<T>): Promise<T> {
  const originals: WindowStubs = {};
  for (const key of Object.keys(overrides) as (keyof WindowStubs)[]) {
    originals[key] = vscode.window[key];
    (vscode.window as unknown as Record<string, unknown>)[key] = overrides[key];
  }
  try {
    return await fn();
  } finally {
    for (const key of Object.keys(overrides) as (keyof WindowStubs)[]) {
      (vscode.window as unknown as Record<string, unknown>)[key] = originals[key];
    }
  }
}

suite('appNotify', () => {
  teardown(() => {
    // Every test must leave the module-level channel unset, matching extension.ts's
    // deactivate() — otherwise a channel from one test could leak state into the next.
    setAppLogChannel(undefined);
  });

  test('showInfo mirrors the message to the app-log channel and still shows the popup', async () => {
    const { channel, entries } = fakeChannel();
    setAppLogChannel(channel);
    let shown: string | undefined;

    await withStubbedWindow(
      {
        showInformationMessage: (msg: string) => {
          shown = msg;
          return Promise.resolve(undefined);
        },
      },
      () => showInfo('Reqnroll: hello'),
    );

    assert.deepStrictEqual(entries, [{ level: 'info', message: 'Reqnroll: hello' }]);
    assert.strictEqual(shown, 'Reqnroll: hello');
  });

  test('showWarn mirrors to the app-log channel at Warning level', async () => {
    const { channel, entries } = fakeChannel();
    setAppLogChannel(channel);

    await withStubbedWindow(
      {
        showWarningMessage: () => Promise.resolve(undefined),
      },
      () => showWarn('Reqnroll: careful'),
    );

    assert.deepStrictEqual(entries, [{ level: 'warn', message: 'Reqnroll: careful' }]);
  });

  test('showError mirrors to the app-log channel at Error level and forwards extra items', async () => {
    const { channel, entries } = fakeChannel();
    setAppLogChannel(channel);
    let receivedItems: string[] = [];

    const choice = await withStubbedWindow(
      {
        showErrorMessage: (_msg: string, ...items: string[]) => {
          receivedItems = items;
          return Promise.resolve(items[0]);
        },
      },
      () => showError('Reqnroll: broken', 'Open Documentation'),
    );

    assert.deepStrictEqual(entries, [{ level: 'error', message: 'Reqnroll: broken' }]);
    assert.deepStrictEqual(receivedItems, ['Open Documentation']);
    assert.strictEqual(choice, 'Open Documentation');
  });

  test('is a no-op mirror when no app-log channel has been set', async () => {
    // setAppLogChannel(undefined) via teardown — simulates a command module under unit test
    // without extension.ts's activate() ever having run.
    const shown = await withStubbedWindow(
      {
        showInformationMessage: () => Promise.resolve(undefined),
      },
      () => showInfo('Reqnroll: no channel set'),
    );

    assert.strictEqual(shown, undefined);
  });
});
