import * as vscode from 'vscode';

/** Opens `uri`, reveals `line`/`char` centered in the editor, and places the cursor there. */
export async function openAndReveal(uri: vscode.Uri, line: number, char: number): Promise<void> {
  const pos = new vscode.Position(line, char);
  const doc = await vscode.workspace.openTextDocument(uri);
  const ed = await vscode.window.showTextDocument(doc);
  ed.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
  ed.selection = new vscode.Selection(pos, pos);
}
