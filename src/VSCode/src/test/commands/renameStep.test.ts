import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import {
  collapseActiveSelectionForFeatureStepRename,
  createRenameMiddleware,
  getRenameTargets,
  pickRenameTarget,
  selectRenameTarget,
} from '../../commands/renameStep';
import { ReqnrollMethods } from '../../lsp/lspMethods';

/** Minimal stand-in for LanguageClient's request/notification surface used by rename disambiguation. */
function fakeClient(overrides: {
  sendRequest?: (method: string, params: unknown) => Promise<unknown>;
  sendNotification?: (method: string, params: unknown) => Promise<void>;
}): LanguageClient {
  return {
    sendRequest: overrides.sendRequest ?? (() => Promise.resolve(null)),
    sendNotification: overrides.sendNotification ?? (() => Promise.resolve(undefined)),
  } as unknown as LanguageClient;
}

suite('renameStep', () => {
  suite('ReqnrollMethods', () => {
    test('defines the rename LSP method names the server implements', () => {
      // Mirrors LspMethodNames.cs — a mismatch here means the client and server drift apart.
      assert.strictEqual(ReqnrollMethods.renameTargets, 'reqnroll/renameTargets');
      assert.strictEqual(ReqnrollMethods.selectRenameTarget, 'reqnroll/selectRenameTarget');
    });
  });

  suite('getRenameTargets', () => {
    test('returns the targets array from a well-formed response', async () => {
      const client = fakeClient({
        sendRequest: () =>
          Promise.resolve({
            targets: [
              { label: 'Given the first number is {int}', expression: 'x', attributeIndex: 0 },
            ],
          }),
      });

      const targets = await getRenameTargets(client, 'file:///Steps.cs', new vscode.Position(0, 0));

      assert.strictEqual(targets.length, 1);
      assert.strictEqual(targets[0].attributeIndex, 0);
    });

    test('returns an empty array when the server responds with no targets', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve(null) });

      const targets = await getRenameTargets(client, 'file:///Steps.cs', new vscode.Position(0, 0));

      assert.deepStrictEqual(targets, []);
    });

    test('returns an empty array when the request throws (e.g. older server)', async () => {
      const client = fakeClient({
        sendRequest: () => Promise.reject(new Error('Unhandled method reqnroll/renameTargets')),
      });

      const targets = await getRenameTargets(client, 'file:///Steps.cs', new vscode.Position(0, 0));

      assert.deepStrictEqual(targets, []);
    });
  });

  suite('pickRenameTarget', () => {
    test('returns the chosen target when the user picks a QuickPick item', async () => {
      // showQuickPick resolves to the picked item when the user selects one; simulate that by
      // stubbing the VS Code API for the duration of this test, following the same pattern as the
      // "dismissed" test below but resolving a real item instead of undefined.
      const original = vscode.window.showQuickPick;
      const targets = [
        { label: 'Given a', expression: 'a', attributeIndex: 0 },
        { label: 'Given b', expression: 'b', attributeIndex: 1 },
      ];
      (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = (
        items: readonly { label: string; description?: string; target: unknown }[],
      ) => Promise.resolve(items[1]);

      try {
        const picked = await pickRenameTarget(targets);

        assert.deepStrictEqual(picked, targets[1]);
      } finally {
        (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = original;
      }
    });

    test('returns undefined when the user dismisses the picker', async () => {
      const original = vscode.window.showQuickPick;
      (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = () =>
        Promise.resolve(undefined);

      try {
        const picked = await pickRenameTarget([
          { label: 'Given a', expression: 'a', attributeIndex: 0 },
        ]);

        assert.strictEqual(picked, undefined);
      } finally {
        (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = original;
      }
    });
  });

  suite('selectRenameTarget', () => {
    test('sends reqnroll/selectRenameTarget with the chosen attributeIndex', async () => {
      let sentMethod: string | undefined;
      let sentParams: unknown;
      const client = fakeClient({
        sendNotification: (method, params) => {
          sentMethod = method;
          sentParams = params;
          return Promise.resolve();
        },
      });

      await selectRenameTarget(client, 'file:///Steps.cs', 1);

      assert.strictEqual(sentMethod, ReqnrollMethods.selectRenameTarget);
      assert.deepStrictEqual(sentParams, {
        uri: 'file:///Steps.cs',
        version: 0,
        attributeIndex: 1,
      });
    });
  });

  // Issue #456: VS Code's built-in rename widget preserves an active pre-selection by reapplying
  // its raw character offset onto whatever placeholder text prepareRename returns — correct for a
  // .cs attribute rename (placeholder IS the literal buffer text) but wrong for a .feature step
  // (placeholder is a different, abstract expression), landing the highlight mid-parameter-token
  // instead of on the word the user meant. Collapsing the selection to a cursor makes VS Code fall
  // back to its own verified-safe default: the whole placeholder selected. This must run before
  // editor.action.rename starts (see extension.ts's reqnroll.renameStep command) rather than from
  // inside prepareRename middleware — mutating the selection while that command has an in-flight
  // request cancels the rename outright for parameterized steps (verified live).
  suite('collapseActiveSelectionForFeatureStepRename', () => {
    const featureDocument = {
      uri: vscode.Uri.parse('file:///Steps.feature'),
      languageId: 'gherkin',
    } as vscode.TextDocument;
    const csDocument = {
      uri: vscode.Uri.parse('file:///Steps.cs'),
      languageId: 'csharp',
    } as vscode.TextDocument;

    function withActiveEditor<T>(editor: vscode.TextEditor | undefined, fn: () => T): T {
      const original = Object.getOwnPropertyDescriptor(vscode.window, 'activeTextEditor');
      Object.defineProperty(vscode.window, 'activeTextEditor', {
        value: editor,
        configurable: true,
      });
      try {
        return fn();
      } finally {
        if (original) Object.defineProperty(vscode.window, 'activeTextEditor', original);
      }
    }

    test('collapses a non-empty selection on a .feature document to its active end', () => {
      const active = new vscode.Position(2, 24);
      const editor = {
        document: featureDocument,
        selection: new vscode.Selection(new vscode.Position(2, 19), active),
      } as vscode.TextEditor;

      withActiveEditor(editor, () => collapseActiveSelectionForFeatureStepRename());

      assert.ok(editor.selection.isEmpty, 'the selection should be collapsed to a plain cursor');
      assert.ok(editor.selection.active.isEqual(active));
    });

    test('does nothing for a .cs document, where the preserved-selection behavior is already correct', () => {
      const originalSelection = new vscode.Selection(
        new vscode.Position(2, 19),
        new vscode.Position(2, 24),
      );
      const editor = { document: csDocument, selection: originalSelection } as vscode.TextEditor;

      withActiveEditor(editor, () => collapseActiveSelectionForFeatureStepRename());

      assert.strictEqual(editor.selection, originalSelection);
    });

    test('does nothing when there is no active selection to begin with', () => {
      const position = new vscode.Position(2, 25);
      const emptySelection = new vscode.Selection(position, position);
      const editor = { document: featureDocument, selection: emptySelection } as vscode.TextEditor;

      withActiveEditor(editor, () => collapseActiveSelectionForFeatureStepRename());

      assert.strictEqual(editor.selection, emptySelection);
    });

    test('does nothing when there is no active editor', () => {
      withActiveEditor(undefined, () => {
        // Must not throw when there's nothing to collapse.
        collapseActiveSelectionForFeatureStepRename();
      });
    });
  });

  suite('createRenameMiddleware', () => {
    const document = { uri: vscode.Uri.parse('file:///Steps.cs') } as vscode.TextDocument;
    const position = new vscode.Position(0, 0);
    const token = {} as vscode.CancellationToken;

    test('delegates straight to next() when there is zero or one target', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve({ targets: [] }) });
      const middleware = createRenameMiddleware(() => client);

      let nextCalled = false;
      const next = () => {
        nextCalled = true;
        return Promise.resolve(new vscode.Range(position, position));
      };

      const result = await middleware.prepareRename!(document, position, token, next);

      assert.ok(nextCalled, 'next() should be invoked for the non-ambiguous case');
      assert.ok(result);
    });

    test('delegates to next() when the client has not started yet', async () => {
      const middleware = createRenameMiddleware(() => undefined);

      let nextCalled = false;
      const next = () => {
        nextCalled = true;
        return Promise.resolve(new vscode.Range(position, position));
      };

      await middleware.prepareRename!(document, position, token, next);

      assert.ok(nextCalled, 'next() should be invoked when the client is not yet available');
    });

    test(
      'sends reqnroll/selectRenameTarget before delegating when multiple targets exist and ' +
        'the user picks one',
      async () => {
        // showQuickPick resolves to the picked item when the user selects one; simulate that by
        // stubbing the VS Code API for the duration of this test, following the same pattern as
        // the "dismisses the picker" test below but resolving a real item instead of undefined.
        const original = vscode.window.showQuickPick;
        (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = (
          items: readonly { label: string; description?: string; target: unknown }[],
        ) => Promise.resolve(items[1]);

        try {
          let sentParams: unknown;
          const client = fakeClient({
            sendRequest: () =>
              Promise.resolve({
                targets: [
                  { label: 'Given a', expression: 'a', attributeIndex: 0 },
                  { label: 'Given b', expression: 'b', attributeIndex: 1 },
                ],
              }),
            sendNotification: (_method, params) => {
              sentParams = params;
              return Promise.resolve();
            },
          });
          const middleware = createRenameMiddleware(() => client);

          let nextCalledBeforeNotification = false;
          let notificationSentBeforeNext = false;
          const next = () => {
            notificationSentBeforeNext = sentParams !== undefined;
            nextCalledBeforeNotification = true;
            return Promise.resolve(new vscode.Range(position, position));
          };

          const result = await middleware.prepareRename!(document, position, token, next);

          assert.ok(nextCalledBeforeNotification, 'next() should be invoked');
          assert.ok(
            notificationSentBeforeNext,
            'reqnroll/selectRenameTarget should be sent before next() runs',
          );
          assert.deepStrictEqual(sentParams, {
            uri: 'file:///Steps.cs',
            version: 0,
            attributeIndex: 1,
          });
          assert.ok(result);
        } finally {
          (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = original;
        }
      },
    );

    test(
      'suppresses rename (does not call next) when multiple targets exist and the user ' +
        'dismisses the picker',
      async () => {
        // showQuickPick resolves to undefined when the user presses Escape; simulate that by
        // stubbing the VS Code API for the duration of this test.
        const original = vscode.window.showQuickPick;
        (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = () =>
          Promise.resolve(undefined);

        try {
          const client = fakeClient({
            sendRequest: () =>
              Promise.resolve({
                targets: [
                  { label: 'Given a', expression: 'a', attributeIndex: 0 },
                  { label: 'Given b', expression: 'b', attributeIndex: 1 },
                ],
              }),
          });
          const middleware = createRenameMiddleware(() => client);

          let nextCalled = false;
          const next = () => {
            nextCalled = true;
            return Promise.resolve(new vscode.Range(position, position));
          };

          const result = await middleware.prepareRename!(document, position, token, next);

          assert.strictEqual(result, undefined);
          assert.strictEqual(
            nextCalled,
            false,
            'next() should not be invoked when the picker is dismissed',
          );
        } finally {
          (vscode.window as unknown as { showQuickPick: unknown }).showQuickPick = original;
        }
      },
    );
  });
});
