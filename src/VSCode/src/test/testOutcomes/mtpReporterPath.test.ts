import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  MTP_REPORTER_ASSEMBLY_FILE_NAME,
  resolveMtpReporterDirectory,
} from '../../testOutcomes/mtpReporterPath';

suite('resolveMtpReporterDirectory', () => {
  const extensionPath = path.join('C:', 'ext');

  test('in production, returns the mtpreporter/ directory when the assembly exists there', () => {
    const dir = path.join(extensionPath, 'mtpreporter');
    const candidate = path.join(dir, MTP_REPORTER_ASSEMBLY_FILE_NAME);

    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Production, extensionPath },
      (p) => p === candidate,
    );

    assert.strictEqual(result, dir);
  });

  test('in production, returns undefined (never throws) when the assembly is not bundled', () => {
    assert.doesNotThrow(() => {
      const result = resolveMtpReporterDirectory(
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
      'Reqnroll.IdeSupport.TestReporter.MTP',
      'bin',
      'Release',
      'net8.0',
    );
    const candidate = path.join(localBuildOutput, MTP_REPORTER_ASSEMBLY_FILE_NAME);

    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Development, extensionPath },
      (p) => p === candidate,
    );

    assert.strictEqual(result, localBuildOutput);
  });

  test('in development, returns undefined when no local build output exists', () => {
    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Development, extensionPath },
      () => false,
    );

    assert.strictEqual(result, undefined);
  });
});
