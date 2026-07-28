import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { doFindStepUsages } from '../../commands/stepUsages';

/** Minimal stand-in for LanguageClient's sendRequest surface used by doFindStepUsages. */
function fakeClient(
  sendRequest: (method: string, params: unknown) => Promise<unknown>,
): LanguageClient {
  return { sendRequest } as unknown as LanguageClient;
}

/** Stubs a vscode.window prompt function for the duration of `fn`, restoring it afterwards. */
async function withStubbedWindow<T>(
  overrides: Partial<{
    showErrorMessage: typeof vscode.window.showErrorMessage;
    showInformationMessage: typeof vscode.window.showInformationMessage;
    showQuickPick: typeof vscode.window.showQuickPick;
  }>,
  fn: () => Promise<T>,
): Promise<T> {
  const originals = { ...overrides };
  for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
    originals[key] = vscode.window[key] as never;
    (vscode.window as unknown as Record<string, unknown>)[key] = overrides[key];
  }
  try {
    return await fn();
  } finally {
    for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
      (vscode.window as unknown as Record<string, unknown>)[key] = originals[key];
    }
  }
}

suite('stepUsages', () => {
  suite('doFindStepUsages', () => {
    test('shows an error message when the request throws', async () => {
      const client = fakeClient(() => Promise.reject(new Error('server unavailable')));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showErrorMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doFindStepUsages(client, 'file:///A.cs', 0, 0),
      );

      assert.match(shownMessage ?? '', /Find Step Usages failed.*server unavailable/);
    });

    test('shows an info message when the cursor is not on a binding', async () => {
      const client = fakeClient(() => Promise.resolve({ isBinding: false, locations: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doFindStepUsages(client, 'file:///A.cs', 0, 0),
      );

      assert.match(shownMessage ?? '', /not on a step definition binding/);
    });

    test('shows an info message when the binding has no usages', async () => {
      const client = fakeClient(() => Promise.resolve({ isBinding: true, locations: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doFindStepUsages(client, 'file:///A.cs', 0, 0),
      );

      assert.match(shownMessage ?? '', /No usages found/);
    });

    test('shows a QuickPick with one item per usage location', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          isBinding: true,
          locations: [
            {
              uri: 'file:///A.feature',
              startLine: 1,
              startChar: 0,
              endLine: 1,
              endChar: 5,
              stepText: 'I do a thing',
            },
            { uri: 'file:///B.feature', startLine: 2, startChar: 0, endLine: 2, endChar: 5 },
          ],
        }),
      );
      let quickPickItems: readonly { label: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { label: string }[]) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doFindStepUsages(client, 'file:///A.cs', 0, 0),
      );

      assert.strictEqual(quickPickItems?.length, 2);
      assert.match(quickPickItems[0].label, /I do a thing/);
      assert.match(quickPickItems[1].label, /B\.feature/);
    });
  });
});
