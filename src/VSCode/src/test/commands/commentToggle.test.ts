import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient, ResponseError } from 'vscode-languageclient/node';
import { doToggleComment } from '../../commands/commentToggle';

suite('commentToggle', () => {
  suite('doToggleComment', () => {
    let editor: vscode.TextEditor;

    suiteSetup(async () => {
      const doc = await vscode.workspace.openTextDocument({
        content: 'line0\nline1\nline2\n',
        language: 'plaintext',
      });
      editor = await vscode.window.showTextDocument(doc);
    });

    test('sends the normalized selection line range to the server', async () => {
      // Selecting from (0,0) to (2,0) is a "dragged past the end of line1 without selecting any
      // character on line2" whole-line selection -- normalizeSelectionLines should exclude line2.
      editor.selection = new vscode.Selection(0, 0, 2, 0);

      let sentParams: { command: string; arguments: unknown[] } | undefined;
      const client = {
        sendRequest: (_type: unknown, params: { command: string; arguments: unknown[] }) => {
          sentParams = params;
          return Promise.resolve(null);
        },
      } as unknown as LanguageClient;

      await doToggleComment(client);

      assert.strictEqual(sentParams?.command, 'reqnroll.toggleComment');
      assert.deepStrictEqual(sentParams?.arguments, [editor.document.uri.toString(), 0, 1]);
    });

    test('shows an error message when the request throws', async () => {
      editor.selection = new vscode.Selection(0, 0, 0, 3);
      const client = {
        sendRequest: () => Promise.reject(new Error('boom')),
      } as unknown as LanguageClient;

      let shownMessage: string | undefined;
      const original = vscode.window.showErrorMessage;
      (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = (
        msg: string,
      ) => {
        shownMessage = msg;
        return Promise.resolve(undefined);
      };

      try {
        await doToggleComment(client);
      } finally {
        (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = original;
      }

      // An unexpected failure is generic: the detail is logged, not shown.
      assert.strictEqual(shownMessage, 'Reqnroll: Comment/Uncomment failed.');
    });

    test('shows a server rejection (-32803) in a modal (issue #1132)', async () => {
      editor.selection = new vscode.Selection(0, 0, 0, 3);
      const client = {
        sendRequest: () =>
          Promise.reject(
            new ResponseError(
              -32803,
              'The document is not open in the Reqnroll language server, so comments cannot be toggled.',
            ),
          ),
      } as unknown as LanguageClient;

      let shownMessage: string | undefined;
      let shownOptions: unknown;
      const original = vscode.window.showErrorMessage;
      (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = (
        msg: string,
        options?: unknown,
      ) => {
        shownMessage = msg;
        shownOptions = options;
        return Promise.resolve(undefined);
      };

      try {
        await doToggleComment(client);
      } finally {
        (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = original;
      }

      assert.match(shownMessage ?? '', /not open in the Reqnroll language server/);
      assert.deepStrictEqual(shownOptions, { modal: true });
    });
  });
});
