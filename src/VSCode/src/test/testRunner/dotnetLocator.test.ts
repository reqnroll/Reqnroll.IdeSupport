import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { resolveDotnetExecutable } from '../../testRunner/dotnetLocator';

suite('dotnetLocator', () => {
  suite('resolveDotnetExecutable', () => {
    const originalPath = process.env.PATH;
    const originalPathCaps = process.env.Path;
    const originalDotnetRoot = process.env.DOTNET_ROOT;
    const originalProgramFiles = process.env['ProgramFiles'];
    const originalProgramFilesX86 = process.env['ProgramFiles(x86)'];
    let tmpDir: string;

    teardown(() => {
      process.env.PATH = originalPath;
      process.env.Path = originalPathCaps;
      if (originalDotnetRoot === undefined) delete process.env.DOTNET_ROOT;
      else process.env.DOTNET_ROOT = originalDotnetRoot;
      if (originalProgramFiles === undefined) delete process.env['ProgramFiles'];
      else process.env['ProgramFiles'] = originalProgramFiles;
      if (originalProgramFilesX86 === undefined) delete process.env['ProgramFiles(x86)'];
      else process.env['ProgramFiles(x86)'] = originalProgramFilesX86;
      if (tmpDir) fs.rmSync(tmpDir, { recursive: true, force: true });
    });

    test('returns the bare command name when dotnet is found on PATH', () => {
      tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'reqnroll-dotnet-locator-'));
      const executableName = os.platform() === 'win32' ? 'dotnet.exe' : 'dotnet';
      fs.writeFileSync(path.join(tmpDir, executableName), '');

      process.env.PATH = tmpDir;
      process.env.Path = tmpDir;

      assert.strictEqual(resolveDotnetExecutable(), 'dotnet');
    });

    test('falls back to DOTNET_ROOT when dotnet is not on PATH', () => {
      tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'reqnroll-dotnet-locator-'));
      const executableName = os.platform() === 'win32' ? 'dotnet.exe' : 'dotnet';
      fs.writeFileSync(path.join(tmpDir, executableName), '');

      process.env.PATH = '';
      process.env.Path = '';
      process.env.DOTNET_ROOT = tmpDir;

      assert.strictEqual(resolveDotnetExecutable(), path.join(tmpDir, executableName));
    });

    test('falls back to the bare command name when nothing resolves', function () {
      // The well-known-install-path fallback is only exercisable cross-platform on Windows,
      // where it's driven by ProgramFiles env vars this test can neutralize; on macOS/Linux
      // it checks hardcoded absolute paths (e.g. /usr/share/dotnet) that may or may not exist
      // on the machine running this test.
      if (os.platform() !== 'win32') this.skip();

      process.env.PATH = '';
      process.env.Path = '';
      delete process.env.DOTNET_ROOT;
      process.env['ProgramFiles'] = '';
      process.env['ProgramFiles(x86)'] = '';

      assert.strictEqual(resolveDotnetExecutable(), 'dotnet');
    });
  });
});
