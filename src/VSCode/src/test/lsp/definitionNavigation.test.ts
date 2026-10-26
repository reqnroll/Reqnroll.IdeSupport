import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  DefinitionNavigationTracker,
  NAVIGATION_WINDOW_MS,
  toDefinitionTargets,
} from '../../lsp/definitionNavigation';

const STEPS = 'file:///c:/w/Steps.cs';
const target = (uri = STEPS, startLine = 10, endLine = 12) => ({ uri, startLine, endLine });

suite('definitionNavigation', () => {
  suite('DefinitionNavigationTracker', () => {
    let clock = 0;
    let reported: number[] = [];
    let tracker: DefinitionNavigationTracker;

    setup(() => {
      clock = 1000;
      reported = [];
      tracker = new DefinitionNavigationTracker(
        (n) => reported.push(n),
        () => clock,
      );
    });

    test('a definition response with no editor movement (hover) reports nothing', () => {
      tracker.noteDefinitionResponse([target()]);
      clock += NAVIGATION_WINDOW_MS * 5;
      tracker.noteEditorPosition('file:///c:/w/Other.feature', 3);
      assert.deepStrictEqual(reported, []);
    });

    test('reports once, with the location count, when the editor lands on a target', () => {
      tracker.noteDefinitionResponse([target(), target('file:///c:/w/More.cs', 1, 1)]);
      clock += 100;
      tracker.noteEditorPosition(STEPS, 11);
      tracker.noteEditorPosition(STEPS, 11);
      assert.deepStrictEqual(reported, [2]);
    });

    test('ignores an editor position outside every target', () => {
      tracker.noteDefinitionResponse([target()]);
      tracker.noteEditorPosition(STEPS, 40);
      tracker.noteEditorPosition('file:///c:/w/Other.cs', 11);
      assert.deepStrictEqual(reported, []);
    });

    test('does not report arrival after the window has passed', () => {
      tracker.noteDefinitionResponse([target()]);
      clock += NAVIGATION_WINDOW_MS + 1;
      tracker.noteEditorPosition(STEPS, 11);
      assert.deepStrictEqual(reported, []);
    });

    test('an empty response clears an earlier one', () => {
      tracker.noteDefinitionResponse([target()]);
      tracker.noteDefinitionResponse([]);
      tracker.noteEditorPosition(STEPS, 11);
      assert.deepStrictEqual(reported, []);
    });
  });

  suite('toDefinitionTargets', () => {
    test('maps Location, Location[] and DefinitionLink[] results', () => {
      const uri = vscode.Uri.parse(STEPS);
      const range = new vscode.Range(4, 0, 6, 1);
      assert.deepStrictEqual(toDefinitionTargets(new vscode.Location(uri, range)), [
        { uri: uri.toString(), startLine: 4, endLine: 6 },
      ]);
      assert.strictEqual(toDefinitionTargets([new vscode.Location(uri, range)]).length, 1);
      const link: vscode.DefinitionLink = {
        targetUri: uri,
        targetRange: new vscode.Range(0, 0, 9, 0),
        targetSelectionRange: range,
      };
      assert.deepStrictEqual(toDefinitionTargets([link]), [
        { uri: uri.toString(), startLine: 4, endLine: 6 },
      ]);
    });

    test('maps null and undefined to no targets', () => {
      assert.deepStrictEqual(toDefinitionTargets(null), []);
      assert.deepStrictEqual(toDefinitionTargets(undefined), []);
    });
  });
});
