import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { doGoToMatchingScenarios } from '../../commands/goToMatchingScenarios';

/** Minimal stand-in for LanguageClient's sendRequest surface used by doGoToMatchingScenarios. */
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

suite('goToMatchingScenarios', () => {
  suite('doGoToMatchingScenarios', () => {
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
        () => doGoToMatchingScenarios(client, 'file:///Hooks.cs', 0, 0),
      );

      assert.match(shownMessage ?? '', /Go to Matching Scenarios failed.*server unavailable/);
    });

    test('shows an info message when the hook has no matching scenarios', async () => {
      const client = fakeClient(() => Promise.resolve({ scenarios: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToMatchingScenarios(client, 'file:///Hooks.cs', 0, 0),
      );

      assert.match(shownMessage ?? '', /no matching scenarios/);
    });

    test('shows a QuickPick with singular wording for exactly one matching scenario', async () => {
      // Matches the VS and Rider clients' equivalent surfaces verbatim ("1 matching scenario") —
      // previously always read "1 matching scenarios" here (issue: wording unification).
      const client = fakeClient(() =>
        Promise.resolve({
          scenarios: [
            {
              uri: 'file:///A.feature',
              startLine: 1,
              startChar: 0,
              scenarioName: 'Refund',
              isOutline: false,
            },
          ],
        }),
      );
      let placeHolder: string | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((
            items: readonly { label: string }[],
            options?: { placeHolder?: string },
          ) => {
            placeHolder = options?.placeHolder;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doGoToMatchingScenarios(client, 'file:///Hooks.cs', 0, 0),
      );

      assert.strictEqual(placeHolder, '1 matching scenario — select to navigate');
    });

    test('shows a QuickPick with plural wording for more than one matching scenario', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          scenarios: [
            {
              uri: 'file:///A.feature',
              startLine: 1,
              startChar: 0,
              scenarioName: 'Refund',
              isOutline: false,
            },
            {
              uri: 'file:///B.feature',
              startLine: 2,
              startChar: 0,
              scenarioName: 'Discount',
              isOutline: false,
            },
          ],
        }),
      );
      let placeHolder: string | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((
            items: readonly { label: string }[],
            options?: { placeHolder?: string },
          ) => {
            placeHolder = options?.placeHolder;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doGoToMatchingScenarios(client, 'file:///Hooks.cs', 0, 0),
      );

      assert.strictEqual(placeHolder, '2 matching scenarios — select to navigate');
    });
  });
});
