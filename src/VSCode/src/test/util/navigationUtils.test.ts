import * as assert from 'assert';
import * as vscode from 'vscode';
import { openAndReveal } from '../../util/navigationUtils';

suite('navigationUtils', () => {
  suite('openAndReveal', () => {
    test('opens the document and places the cursor at the given position', async () => {
      const doc = await vscode.workspace.openTextDocument({
        content: 'line0\nline1\nline2\n',
        language: 'plaintext',
      });

      await openAndReveal(doc.uri, 1, 2);

      const editor = vscode.window.activeTextEditor;
      assert.ok(editor, 'expected an active editor after openAndReveal');
      assert.strictEqual(editor.document.uri.toString(), doc.uri.toString());
      assert.strictEqual(editor.selection.active.line, 1);
      assert.strictEqual(editor.selection.active.character, 2);
      assert.strictEqual(editor.selection.anchor.line, 1);
      assert.strictEqual(editor.selection.anchor.character, 2);
    });

    test('reveals a target line well past the initial viewport', async () => {
      // `revealRange` schedules a layout rather than applying one synchronously (and the
      // returned `TextEditor`'s methods aren't reassignable to spy on, unlike `vscode.window`'s
      // own top-level functions), so this polls the real `visibleRanges` for a bit rather than
      // asserting on it immediately -- still exercising the real API rather than a mock of it.
      const lines = Array.from({ length: 400 }, (_, i) => `line${i}`).join('\n');
      const doc = await vscode.workspace.openTextDocument({
        content: lines,
        language: 'plaintext',
      });

      await openAndReveal(doc.uri, 300, 0);

      const editor = vscode.window.activeTextEditor!;
      const target = new vscode.Position(300, 0);
      const deadline = Date.now() + 3000;
      let visible = false;
      while (Date.now() < deadline) {
        visible = editor.visibleRanges.some((range) => range.contains(target));
        if (visible) break;
        await new Promise((resolve) => setTimeout(resolve, 50));
      }

      assert.strictEqual(visible, true, 'target line should become visible after reveal');
    });
  });
});
