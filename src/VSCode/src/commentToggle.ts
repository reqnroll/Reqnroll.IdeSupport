import * as vscode from 'vscode';
import { ExecuteCommandRequest, LanguageClient } from 'vscode-languageclient/node';
import { normalizeSelectionLines } from './selectionUtils';

/**
 * Toggles line comments on the active editor's selection by asking the server to compute the
 * edit (via the `reqnroll.toggleComment` command) for the selected line range.
 */
export async function doToggleComment(client: LanguageClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;

  const sel = editor.selection;
  const [startLine, endLine] = normalizeSelectionLines(
    sel.start.line,
    sel.end.line,
    sel.end.character,
  );

  try {
    await client.sendRequest(ExecuteCommandRequest.type, {
      command: 'reqnroll.toggleComment',
      arguments: [editor.document.uri.toString(), startLine, endLine],
    });
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Comment/Uncomment failed — ${msg}`);
  }
}
