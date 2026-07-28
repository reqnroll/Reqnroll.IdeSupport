import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import { ridFor, serverBinaryName, resolveServerPath } from '../extension';

suite('resolveServerPath', () => {
  suite('ridFor', () => {
    test('maps win32/x64 to win-x64 and win32/arm64 to win-arm64', () => {
      assert.strictEqual(ridFor('win32', 'x64'), 'win-x64');
      assert.strictEqual(ridFor('win32', 'arm64'), 'win-arm64');
    });

    test('maps darwin/x64 to osx-x64 and darwin/arm64 to osx-arm64', () => {
      assert.strictEqual(ridFor('darwin', 'x64'), 'osx-x64');
      assert.strictEqual(ridFor('darwin', 'arm64'), 'osx-arm64');
    });

    test('maps any other platform to linux-x64', () => {
      assert.strictEqual(ridFor('linux', 'x64'), 'linux-x64');
    });
  });

  suite('serverBinaryName', () => {
    test('appends .exe on win32', () => {
      assert.strictEqual(serverBinaryName('win32'), 'Reqnroll.IdeSupport.LSP.Server.exe');
    });

    test('has no extension on other platforms', () => {
      assert.strictEqual(serverBinaryName('darwin'), 'Reqnroll.IdeSupport.LSP.Server');
      assert.strictEqual(serverBinaryName('linux'), 'Reqnroll.IdeSupport.LSP.Server');
    });
  });

  suite('resolveServerPath', () => {
    const extensionPath = path.join('C:', 'ext');
    const rid = ridFor(process.platform, process.arch);
    const binaryName = serverBinaryName(process.platform);

    test('in production, returns the server/<rid>/ candidate when it exists', () => {
      const candidate = path.join(extensionPath, 'server', rid, binaryName);
      const result = resolveServerPath(
        { extensionMode: vscode.ExtensionMode.Production, extensionPath },
        (p) => p === candidate,
      );

      assert.strictEqual(result, candidate);
    });

    test('in production, falls back to the legacy server/ path when the rid-specific one is missing', () => {
      const legacy = path.join(extensionPath, 'server', binaryName);
      const result = resolveServerPath(
        { extensionMode: vscode.ExtensionMode.Production, extensionPath },
        (p) => p === legacy,
      );

      assert.strictEqual(result, legacy);
    });

    test('in production, throws when neither the rid-specific nor legacy path exists', () => {
      assert.throws(
        () =>
          resolveServerPath(
            { extensionMode: vscode.ExtensionMode.Production, extensionPath },
            () => false,
          ),
        /Reqnroll LSP server not found/,
      );
    });

    test('in development, returns the local build output when it exists', () => {
      const localBuildOutput = path.join(
        extensionPath,
        '..',
        '..',
        'src',
        'LSP',
        'Reqnroll.IdeSupport.LSP.Server',
        'bin',
        'Release',
        'net10.0',
        rid,
        'publish',
        binaryName,
      );
      const result = resolveServerPath(
        { extensionMode: vscode.ExtensionMode.Development, extensionPath },
        (p) => p === localBuildOutput,
      );

      assert.strictEqual(result, localBuildOutput);
    });

    test('in development, falls back to the server/<rid>/ layout when no local build output exists', () => {
      const result = resolveServerPath(
        { extensionMode: vscode.ExtensionMode.Development, extensionPath },
        () => false,
      );

      assert.strictEqual(result, path.join(extensionPath, 'server', rid, binaryName));
    });
  });
});
