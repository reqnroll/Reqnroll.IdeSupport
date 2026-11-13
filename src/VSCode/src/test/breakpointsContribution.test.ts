import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { REQNROLL_LANGUAGE_ID } from '../languageIds';

/**
 * Tests for the `breakpoints` contribution in package.json.
 *
 * VS Code only lets the user set a breakpoint in an editor whose language some extension has
 * contributed to `breakpoints` (or when `debug.allowBreakpointsEverywhere` is on). It then sends
 * those breakpoints to every debug session, so the .NET debugger started by C# Dev Kit receives
 * them and binds them through the `#line` pragmas Reqnroll writes into the generated test code.
 */

interface BreakpointContribution {
  language: string;
  when?: string;
}

interface PackageManifest {
  contributes: {
    breakpoints?: BreakpointContribution[];
  };
}

let manifest: PackageManifest;

suite('package.json breakpoints contribution', () => {
  suiteSetup(() => {
    const manifestPath = path.resolve(__dirname, '..', '..', 'package.json');
    manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf-8')) as PackageManifest;
  });

  test('always allows breakpoints for the Reqnroll language', () => {
    const entry = manifest.contributes.breakpoints?.find(
      (b) => b.language === REQNROLL_LANGUAGE_ID,
    );
    assert.ok(entry, `no breakpoints contribution for language '${REQNROLL_LANGUAGE_ID}'`);
    assert.strictEqual(entry.when, undefined, 'the contribution should not be conditional');
  });
});
