import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  TEST_LOGGER_ASSEMBLY_FILE_NAME,
  resolveTestLoggerDirectory,
} from '../../testOutcomes/testLoggerPath';

suite('resolveTestLoggerDirectory', () => {
  const extensionPath = path.join('C:', 'ext');

  test('in production, returns the testlogger/ directory when the assembly exists there', () => {
    const dir = path.join(extensionPath, 'testlogger');
    const candidate = path.join(dir, TEST_LOGGER_ASSEMBLY_FILE_NAME);

    const result = resolveTestLoggerDirectory(
      { extensionMode: vscode.ExtensionMode.Production, extensionPath },
      (p) => p === candidate,
    );

    assert.strictEqual(result, dir);
  });

  test('in production, returns undefined (never throws) when the assembly is not bundled', () => {
    assert.doesNotThrow(() => {
      const result = resolveTestLoggerDirectory(
        { extensionMode: vscode.ExtensionMode.Production, extensionPath },
        () => false,
      );
      assert.strictEqual(result, undefined);
    });
  });

  test('in development, returns the local build output directory when it exists', () => {
    const localBuildOutput = path.join(
      extensionPath,
      '..',
      '..',
      'src',
      'Core',
      'Reqnroll.IdeSupport.TestLogger',
      'bin',
      'Release',
      'netstandard2.0',
    );
    const candidate = path.join(localBuildOutput, TEST_LOGGER_ASSEMBLY_FILE_NAME);

    const result = resolveTestLoggerDirectory(
      { extensionMode: vscode.ExtensionMode.Development, extensionPath },
      (p) => p === candidate,
    );

    assert.strictEqual(result, localBuildOutput);
  });

  test('in development, returns undefined when no local build output exists', () => {
    const result = resolveTestLoggerDirectory(
      { extensionMode: vscode.ExtensionMode.Development, extensionPath },
      () => false,
    );

    assert.strictEqual(result, undefined);
  });
});
