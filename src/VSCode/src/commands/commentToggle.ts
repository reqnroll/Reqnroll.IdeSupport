import * as vscode from 'vscode';
import { ExecuteCommandRequest, LanguageClient } from 'vscode-languageclient/node';
import { logWarn, showEditRejected, showError } from '../logging/appNotify';
import { asEditRejection } from '../lsp/editRejection';
import { normalizeSelectionLines } from '../util/selectionUtils';

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
    // A user-correctable rejection (-32803: e.g. the document is not open in the server) is shown
    // in a modal; any other failure is unexpected — log the detail, show it generically.
    const rejection = asEditRejection(err);
    if (rejection !== undefined) {
      void showEditRejected(rejection);
      return;
    }
    logWarn(`Comment/Uncomment failed: ${err instanceof Error ? err.message : String(err)}`);
    void showError('Reqnroll: Comment/Uncomment failed.');
  }
}
