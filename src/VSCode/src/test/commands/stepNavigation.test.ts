import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import {
  distinctByPosition,
  doGoToStepDefinition,
  resolveRelativePathIn,
} from '../../commands/stepNavigation';

type QuickPickRow = { label: string; description?: string; detail?: string };

/** Stubs vscode.window prompt functions for the duration of `fn`, restoring them afterwards. */
async function withStubbedWindow<T>(
  overrides: Partial<{
    showInformationMessage: typeof vscode.window.showInformationMessage;
    showQuickPick: typeof vscode.window.showQuickPick;
    showWarningMessage: typeof vscode.window.showWarningMessage;
  }>,
  fn: () => Promise<T>,
): Promise<T> {
  const originals = { ...overrides };
  for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
    originals[key] = vscode.window[key] as never;
    (vscode.window as unknown as Record<string, unknown>)[key] = overrides[key];
  }
  try {
    return await fn();
  } finally {
    for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
      (vscode.window as unknown as Record<string, unknown>)[key] = originals[key];
    }
  }
}

// Folders/files are built with path.join off the real OS root (not hardcoded "C:\\..." literals)
// so these tests exercise vscode.Uri#fsPath's actual, platform-specific separator convention --
// this suite runs in the real Extension Host, on Linux CI runners as well as Windows.
suite('stepNavigation', () => {
  // ── doGoToStepDefinition (reqnroll/goToStepDefinition, issue #757) ──────────

  suite('doGoToStepDefinition', () => {
    const stepsFile = path.join(path.parse(process.cwd()).root, 'work', 'Steps.cs');

    suiteSetup(async () => {
      // The command reads the caret from the active editor, so give it one.
      const doc = await vscode.workspace.openTextDocument({
        content: 'Given the first number is 50',
      });
      await vscode.window.showTextDocument(doc);
    });

    suiteTeardown(async () => {
      await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    });

    test('sends reqnroll/goToStepDefinition and reports when there is no binding', async () => {
      let sentMethod: string | undefined;
      const client = {
        sendRequest: (method: string) => {
          sentMethod = method;
          return Promise.resolve({ items: [] });
        },
      } as unknown as LanguageClient;
      let info: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            info = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToStepDefinition(client),
      );

      assert.strictEqual(sentMethod, 'reqnroll/goToStepDefinition');
      assert.match(info ?? '', /No step definition found/);
    });

    test('lists several bindings with their method and attribute', async () => {
      const client = {
        sendRequest: () =>
          Promise.resolve({
            items: [
              {
                className: 'CalculatorSteps',
                methodName: 'GivenTheFirstNumberIs',
                bindingExpression: 'the first number is {int}',
                stepDefinitionType: 'Given',
                sourceFile: stepsFile,
                sourceLine: 16,
                sourceChar: 20,
              },
              {
                className: 'CalculatorSteps',
                methodName: 'Given_the_first_number_is_P0',
                stepDefinitionType: 'Given',
                sourceFile: stepsFile,
                sourceLine: 24,
                sourceChar: 20,
              },
            ],
          }),
      } as unknown as LanguageClient;
      let rows: readonly QuickPickRow[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly QuickPickRow[]) => {
            rows = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doGoToStepDefinition(client),
      );

      assert.strictEqual(rows?.length, 2);
      assert.match(rows[0].label, /CalculatorSteps\.GivenTheFirstNumberIs/);
      assert.strictEqual(rows[0].description, '[Given("the first number is {int}")]');
      assert.match(rows[0].detail ?? '', /Steps\.cs:17$/);
      assert.strictEqual(rows[1].description, '[Given]');
    });

    test('explains a single binding whose source is not on this machine', async () => {
      const client = {
        sendRequest: () =>
          Promise.resolve({
            items: [
              {
                className: 'Steps',
                methodName: 'AStep',
                sourceLine: 3,
                sourceChar: 0,
                isResolved: false,
                recordedSourceFile: '/workspaces/host/Steps.cs',
              },
            ],
          }),
      } as unknown as LanguageClient;
      let warning: string | undefined;

      await withStubbedWindow(
        {
          showWarningMessage: (msg: string) => {
            warning = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToStepDefinition(client),
      );

      assert.match(warning ?? '', /isn't on this machine/);
      assert.match(warning ?? '', /\/workspaces\/host\/Steps\.cs/);
    });
  });

  suite('distinctByPosition', () => {
    test('collapses bindings at the same source position, keeping order', () => {
      // One method with two matching attributes: two bindings, one navigation target.
      const at = (methodName: string, sourceLine: number) => ({
        methodName,
        sourceFile: '/ws/Steps.cs',
        sourceLine,
        sourceChar: 8,
      });

      const result = distinctByPosition([at('A', 10), at('A', 10), at('B', 20)]);

      assert.deepStrictEqual(
        result.map((r) => r.methodName),
        ['A', 'B'],
      );
    });
  });

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
