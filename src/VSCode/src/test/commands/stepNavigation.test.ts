import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import { resolveRelativePathIn } from '../../commands/stepNavigation';

// Folders/files are built with path.join off the real OS root (not hardcoded "C:\\..." literals)
// so these tests exercise vscode.Uri#fsPath's actual, platform-specific separator convention --
// this suite runs in the real Extension Host, on Linux CI runners as well as Windows.
suite('stepNavigation', () => {
  suite('resolveRelativePathIn', () => {
    const root = path.parse(process.cwd()).root;
    const folder = path.join(root, 'work', 'Repo');

    test('returns the path relative to the containing workspace folder', () => {
      const file = path.join(folder, 'Sub', 'Steps.cs');
      const result = resolveRelativePathIn(vscode.Uri.file(file).toString(), [folder]);

      assert.strictEqual(result, path.join('Sub', 'Steps.cs'));
    });

    test('matches case-insensitively when the file path is cased differently than the workspace folder (issue #324)', function () {
      // A .NET LSP server can normalize a file URI's casing (e.g. a lowercased Windows drive
      // letter, file:///c:/...) differently than the workspace folder's fsPath casing. That's a
      // Windows-only scenario -- POSIX paths are case-sensitive, so path.relative() there has no
      // reason to treat "repo" and "Repo" as the same directory, and asserting a specific relative
      // path in that case would test path.relative()'s behavior, not resolveRelativePathIn's.
      if (process.platform !== 'win32') {
        this.skip();
        return;
      }

      const file = path.join(root, 'work', 'repo', 'Sub', 'Steps.cs');
      const result = resolveRelativePathIn(vscode.Uri.file(file).toString(), [folder]);

      assert.strictEqual(result, path.join('Sub', 'Steps.cs'));
    });

    test('falls back to the bare filename when no folder contains the file', () => {
      const file = path.join(root, 'elsewhere', 'Steps.cs');
      const result = resolveRelativePathIn(vscode.Uri.file(file).toString(), [folder]);

      assert.strictEqual(result, 'Steps.cs');
    });

    test('falls back to the bare filename when there are no workspace folders', () => {
      const file = path.join(folder, 'Steps.cs');
      const result = resolveRelativePathIn(vscode.Uri.file(file).toString(), []);

      assert.strictEqual(result, 'Steps.cs');
    });

    test('returns the raw string on an unparsable URI instead of throwing', () => {
      const result = resolveRelativePathIn('not a uri', [folder]);

      assert.strictEqual(result, 'not a uri');
    });
  });
});
