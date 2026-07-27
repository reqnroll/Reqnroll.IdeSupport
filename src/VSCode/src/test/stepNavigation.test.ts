import * as assert from 'assert';
import { resolveRelativePathIn } from '../stepNavigation';

suite('stepNavigation', () => {
  suite('resolveRelativePathIn', () => {
    test('returns the path relative to the containing workspace folder', () => {
      const result = resolveRelativePathIn('file:///C:/work/Repo/Sub/Steps.cs', [
        'C:\\work\\Repo',
      ]);

      assert.strictEqual(result, 'Sub\\Steps.cs');
    });

    test('matches case-insensitively when the URI drive letter is cased differently (issue #324)', () => {
      // A .NET LSP server can normalize a file URI's drive letter to lowercase
      // (file:///c:/...) while the workspace folder's fsPath keeps the user's original casing.
      const result = resolveRelativePathIn('file:///c:/work/repo/Sub/Steps.cs', [
        'C:\\work\\Repo',
      ]);

      assert.strictEqual(result, 'Sub\\Steps.cs');
    });

    test('falls back to the bare filename when no folder contains the file', () => {
      const result = resolveRelativePathIn('file:///C:/elsewhere/Steps.cs', ['C:\\work\\Repo']);

      assert.strictEqual(result, 'Steps.cs');
    });

    test('falls back to the bare filename when there are no workspace folders', () => {
      const result = resolveRelativePathIn('file:///C:/work/Repo/Steps.cs', []);

      assert.strictEqual(result, 'Steps.cs');
    });

    test('returns the raw string on an unparsable URI instead of throwing', () => {
      const result = resolveRelativePathIn('not a uri', ['C:\\work\\Repo']);

      assert.strictEqual(result, 'not a uri');
    });
  });
});
