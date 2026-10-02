import * as assert from 'assert';
import * as path from 'path';
import {
  resolveApplicationDirectory,
  resolveLogDirectory,
  pruneOldLogs,
} from '../../logging/logPaths';

// resolveApplicationDirectory's per-OS branches mirror the Rider plugin's
// ReqnrollDebugLogger.logDirectory and the .NET side's ReqnrollLogPaths.ResolveApplicationDirectory,
// both of which are unit-tested against injected platform/env values (issue #625/#626). This module
// reads process.platform directly rather than taking it as a parameter — matching the convention
// already in place before this file existed (the function it was extracted from,
// lspInspectorLogger.ts's original private resolveLogDirectory, was never tested per-OS either) —
// so this is a smoke test against whatever platform the test host actually runs on, not a
// per-branch test like its .NET/Kotlin siblings.
suite('resolveApplicationDirectory', () => {
  test('returns a Reqnroll directory path without throwing', () => {
    const dir = resolveApplicationDirectory();
    assert.ok(dir.endsWith('Reqnroll'), `expected a path ending in "Reqnroll" but got: ${dir}`);
  });
});

suite('resolveLogDirectory', () => {
  test('is a logs subfolder of resolveApplicationDirectory (issue #726)', () => {
    const dir = resolveLogDirectory();
    assert.strictEqual(dir, path.join(resolveApplicationDirectory(), 'logs'));
  });
});

suite('pruneOldLogs', () => {
  test('does not throw for a directory that does not exist', () => {
    assert.doesNotThrow(() => pruneOldLogs('/does/not/exist/reqnroll-test-dir'));
  });
});
