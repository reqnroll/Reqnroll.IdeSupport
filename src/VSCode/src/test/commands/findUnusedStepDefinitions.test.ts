import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { doFindUnusedStepDefinitions } from '../../commands/findUnusedStepDefinitions';

/** Minimal stand-in for LanguageClient's sendRequest surface used by doFindUnusedStepDefinitions. */
function fakeClient(sendRequest: () => Promise<unknown>): LanguageClient {
  return { sendRequest } as unknown as LanguageClient;
}

/** Stubs a vscode.window prompt function for the duration of `fn`, restoring it afterwards. */
async function withStubbedWindow<T>(
  overrides: Partial<{
    showErrorMessage: typeof vscode.window.showErrorMessage;
    showInformationMessage: typeof vscode.window.showInformationMessage;
    showQuickPick: typeof vscode.window.showQuickPick;
    showWarningMessage: typeof vscode.window.showWarningMessage;
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

suite('findUnusedStepDefinitions', () => {
  suite('doFindUnusedStepDefinitions', () => {
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
        () => doFindUnusedStepDefinitions(client),
      );

      assert.match(shownMessage ?? '', /Find Unused Step Definitions failed.*boom/);
    });

    test('shows an info message when there are no unused step definitions', async () => {
      const client = fakeClient(() => Promise.resolve({ items: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doFindUnusedStepDefinitions(client),
      );

      assert.match(shownMessage ?? '', /No unused step definitions found/);
    });

    test('shows a QuickPick with a label built from class and method name', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          items: [
            {
              className: 'MySteps',
              methodName: 'GivenSomething',
              bindingExpression: 'I have something',
              projectName: 'MyProject',
              sourceLine: 3,
              sourceChar: 0,
            },
          ],
        }),
      );
      let quickPickItems: readonly { label: string; description?: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { label: string; description?: string }[]) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doFindUnusedStepDefinitions(client),
      );

      assert.strictEqual(quickPickItems?.length, 1);
      assert.match(quickPickItems[0].label, /MySteps\.GivenSomething/);
      assert.strictEqual(quickPickItems[0].description, 'I have something');
    });

    // ── Unresolvable source paths (issue #540) ──────────────────────────────

    test('marks an entry whose source is not on this machine', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          items: [
            {
              className: 'MySteps',
              methodName: 'GivenSomething',
              projectName: 'MyProject',
              sourceLine: 3,
              sourceChar: 0,
              isResolved: false,
              recordedSourceFile: '/workspaces/host-solution/Specs/Steps.cs',
            },
          ],
        }),
      );
      let quickPickItems:
        readonly { label: string; description?: string; detail?: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((
            items: readonly { label: string; description?: string; detail?: string }[],
          ) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doFindUnusedStepDefinitions(client),
      );

      assert.match(quickPickItems?.[0].label ?? '', /\$\(error\)/);
      assert.match(quickPickItems?.[0].detail ?? '', /source not on this machine/);
    });

    test('explains rather than silently doing nothing when the picked entry has no source file', async () => {
      const item = {
        className: 'MySteps',
        methodName: 'GivenSomething',
        projectName: 'MyProject',
        sourceLine: 3,
        sourceChar: 0,
        isResolved: false,
        recordedSourceFile: '/workspaces/host-solution/Specs/Steps.cs',
      };
      const client = fakeClient(() => Promise.resolve({ items: [item] }));
      let warning: string | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { item: unknown }[]) =>
            Promise.resolve(items[0])) as unknown as typeof vscode.window.showQuickPick,
          showWarningMessage: (msg: string) => {
            warning = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doFindUnusedStepDefinitions(client),
      );

      assert.match(warning ?? '', /isn't on this machine/);
      assert.match(warning ?? '', /\/workspaces\/host-solution\/Specs\/Steps\.cs/);
    });

    test('treats an item from an older server with no isResolved field as navigable', async () => {
      // Back-compat: the field is absent in responses from a server predating issue #540.
      const client = fakeClient(() =>
        Promise.resolve({
          items: [
            {
              className: 'MySteps',
              methodName: 'GivenSomething',
              sourceFile: '/ws/Steps.cs',
              sourceLine: 3,
              sourceChar: 0,
            },
          ],
        }),
      );
      let quickPickItems: readonly { label: string; detail?: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { label: string; detail?: string }[]) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doFindUnusedStepDefinitions(client),
      );

      assert.match(quickPickItems?.[0].label ?? '', /\$\(warning\)/);
      assert.doesNotMatch(quickPickItems?.[0].detail ?? '', /not on this machine/);
    });
  });
});
