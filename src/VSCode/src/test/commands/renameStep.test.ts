import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import {
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
