import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { doGoToHooks } from '../../commands/goToHooks';

/** Minimal stand-in for LanguageClient's sendRequest surface used by doGoToHooks. */
function fakeClient(sendRequest: () => Promise<unknown>): LanguageClient {
  return { sendRequest } as unknown as LanguageClient;
}

/** Stand-in that also captures the request payload passed to sendRequest. */
function capturingClient(
  response: unknown,
  onRequest: (method: unknown, params: unknown) => void,
): LanguageClient {
  return {
    sendRequest: (method: unknown, params: unknown) => {
      onRequest(method, params);
      return Promise.resolve(response);
    },
  } as unknown as LanguageClient;
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

suite('goToHooks', () => {
  suite('doGoToHooks', () => {
    let editor: vscode.TextEditor;

    suiteSetup(async () => {
      const doc = await vscode.workspace.openTextDocument({
        content: 'irrelevant',
        language: 'plaintext',
      });
      editor = await vscode.window.showTextDocument(doc);
    });

    test('shows an error message when the request throws', async () => {
      const client = fakeClient(() => Promise.reject(new Error('boom')));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showErrorMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToHooks(client),
      );

      assert.match(shownMessage ?? '', /Go to Hooks failed.*boom/);
    });

    test('shows an info message when no hooks are found', async () => {
      const client = fakeClient(() => Promise.resolve({ hooks: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToHooks(client),
      );

      assert.match(shownMessage ?? '', /No hooks found/);
    });

    test('shows a QuickPick with an order detail only when hookOrder is non-zero', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 5,
              methodName: 'Setup',
            },
            {
              uri: editor.document.uri.toString(),
              startLine: 1,
              startChar: 0,
              hookType: 'AfterScenario',
              hookOrder: 0,
              methodName: 'Teardown',
            },
          ],
        }),
      );
      let quickPickItems: readonly { detail?: string; description?: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { detail?: string; description?: string }[]) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doGoToHooks(client),
      );

      assert.strictEqual(quickPickItems?.length, 2);
      assert.strictEqual(quickPickItems[0].detail, 'Order: 5');
      assert.strictEqual(quickPickItems[1].detail, undefined);
    });

    test('navigates directly for a single hook when alwaysShowPicker is not set (manual invocation)', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 0,
              methodName: 'Setup',
            },
          ],
        }),
      );
      let quickPickShown = false;

      await withStubbedWindow(
        {
          showQuickPick: () => {
            quickPickShown = true;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToHooks(client, { uri: editor.document.uri.toString(), line: 0, character: 0 }),
      );

      assert.strictEqual(quickPickShown, false);
    });

    test('shows the QuickPick for a single hook when alwaysShowPicker is set (CodeLens click)', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 0,
              methodName: 'Setup',
            },
          ],
        }),
      );
      let quickPickPlaceholder: string | undefined;

      await withStubbedWindow(
        {
          showQuickPick: (_items: unknown, options?: { placeHolder?: string }) => {
            quickPickPlaceholder = options?.placeHolder;
            return Promise.resolve(undefined);
          },
        },
        () =>
          doGoToHooks(client, {
            uri: editor.document.uri.toString(),
            line: 0,
            character: 0,
            alwaysShowPicker: true,
          }),
      );

      assert.match(quickPickPlaceholder ?? '', /^1 hook found/);
    });

    test('sends ownLevelOnly=false by default (manual invocation)', async () => {
      let sentParams: unknown;
      const client = capturingClient({ hooks: [] }, (_method, params) => {
        sentParams = params;
      });

      await doGoToHooks(client, { uri: editor.document.uri.toString(), line: 0, character: 0 });

      assert.strictEqual((sentParams as { ownLevelOnly?: boolean })?.ownLevelOnly, false);
    });

    test('forwards ownLevelOnly=true when passed by a CodeLens click', async () => {
      let sentParams: unknown;
      const client = capturingClient({ hooks: [] }, (_method, params) => {
        sentParams = params;
      });

      await doGoToHooks(client, {
        uri: editor.document.uri.toString(),
        line: 0,
        character: 0,
        ownLevelOnly: true,
      });

      assert.strictEqual((sentParams as { ownLevelOnly?: boolean })?.ownLevelOnly, true);
    });
  });
});
