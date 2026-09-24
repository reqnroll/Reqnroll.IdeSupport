import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME,
  MTP_REPORTER_SOURCE_DIRECTORY_NAME,
  resolveMtpReporterDirectory,
} from '../../testOutcomes/mtpReporterPath';

/** An existsSync fake that reports a complete bundle (the .targets file AND ReporterSource/) in `dir`. */
const completeBundleIn = (dir: string) => (p: string) =>
  p === path.join(dir, MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME) ||
  p === path.join(dir, MTP_REPORTER_SOURCE_DIRECTORY_NAME);

suite('resolveMtpReporterDirectory', () => {
  const extensionPath = path.join('C:', 'ext');

  test('in production, returns the mtpreporter/ directory when the source bundle is there', () => {
    const dir = path.join(extensionPath, 'mtpreporter');

    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Production, extensionPath },
      completeBundleIn(dir),
    );

    assert.strictEqual(result, dir);
  });

  test('in production, returns undefined for an incomplete bundle without ReporterSource/', () => {
    const targets = path.join(extensionPath, 'mtpreporter', MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME);

    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Production, extensionPath },
      (p) => p === targets,
    );

    assert.strictEqual(result, undefined);
  });

  test('in production, returns undefined (never throws) when the bundle is not there', () => {
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
      'bundle',
    );
    const result = resolveMtpReporterDirectory(
      { extensionMode: vscode.ExtensionMode.Development, extensionPath },
      completeBundleIn(localBuildOutput),
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
