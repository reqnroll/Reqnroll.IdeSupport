import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
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

      assert.match(shownMessage ?? '', /Comment\/Uncomment failed.*boom/);
    });
  });
});
