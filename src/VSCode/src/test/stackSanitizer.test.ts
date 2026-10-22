import * as assert from 'assert';
import { EXTERNAL_PLACEHOLDER, MAX_FRAMES, MAX_LENGTH, sanitizeStack } from '../stackSanitizer';

const ROOT =
  'C:\\Users\\John Smith\\.vscode\\extensions\\reqnroll.reqnroll-ide-support-vscode-1.2.3';
const OWN = `${ROOT}\\dist\\extension.js`;

suite('stackSanitizer (#1199)', () => {
  test('names own frames as function:line and collapses foreign runs to one placeholder', () => {
    const stack = [
      'Error: boom',
      `    at formatDocument (${OWN}:120:15)`,
      '    at async Object.run (C:\\Program Files\\Microsoft VS Code\\resources\\app\\out\\a.js:1:2)',
      '    at node:internal/process/task_queues:95:5',
      `    at new Handler (${OWN}:7:1)`,
    ].join('\n');

    assert.strictEqual(
      sanitizeStack(stack, ROOT),
      `formatDocument:120\n${EXTERNAL_PLACEHOLDER}\nHandler:7`,
    );
  });

  test('never emits paths, file names, columns, the message or the user name', () => {
    const stack = `Error: failed on C:\\secret\\a.feature\n    at run (${OWN}:5:9)`;

    const result = sanitizeStack(stack, ROOT)!;

    for (const leak of [
      'John',
      'Smith',
      'secret',
      'a.feature',
      'extension.js',
      ':9',
      'failed',
      'C:',
    ]) {
      assert.ok(!result.includes(leak), `leaked ${leak}: ${result}`);
    }
  });

  test('accepts file URIs and POSIX roots, ignoring case', () => {
    const root = '/home/Bob/.vscode/extensions/reqnroll-1';
    const stack =
      'Error: x\n    at go (file:///home/bob/.vscode/extensions/reqnroll-1/dist/extension.js:3:4)';

    assert.strictEqual(sanitizeStack(stack, root), 'go:3');
  });

  test('a dependency inside the install directory is not the extensions own code', () => {
    const stack = [
      'Error: x',
      `    at callFn (${ROOT}\\node_modules\\mocha\\lib\\runnable.js:360:23)`,
      `    at run (${OWN}:5:9)`,
    ].join('\n');

    assert.strictEqual(sanitizeStack(stack, ROOT), `${EXTERNAL_PLACEHOLDER}\nrun:5`);
  });

  test('a lookalike sibling directory is not the extension', () => {
    const stack = `Error: x\n    at go (${ROOT}-evil\\dist\\extension.js:3:4)`;

    assert.strictEqual(sanitizeStack(stack, ROOT), undefined);
  });

  test('strips async/new/alias decorations and anything that is not an identifier', () => {
    const stack = [
      'Error: x',
      `    at async Foo.bar [as baz] (${OWN}:1:1)`,
      `    at <anonymous> (${OWN}:2:1)`,
      `    at ev"il(){} (${OWN}:3:1)`,
    ].join('\n');

    assert.strictEqual(sanitizeStack(stack, ROOT), 'Foo.bar:1\n{anonymous}:2\nevil:3');
  });

  test('returns undefined without a root, a stack or an own frame', () => {
    assert.strictEqual(sanitizeStack(`Error\n    at f (${OWN}:1:1)`, undefined), undefined);
    assert.strictEqual(sanitizeStack(undefined, ROOT), undefined);
    assert.strictEqual(sanitizeStack('Error: x\n    at f (/usr/lib/node.js:1:1)', ROOT), undefined);
  });

  test('is bounded in entries and length', () => {
    const many = ['Error: x']
      .concat(Array.from({ length: 50 }, (_, i) => `    at f${i} (${OWN}:${i + 1}:1)`))
      .join('\n');

    const result = sanitizeStack(many, ROOT)!;

    assert.strictEqual(result.split('\n').length, MAX_FRAMES);
    assert.ok(result.length <= MAX_LENGTH);
  });
});
