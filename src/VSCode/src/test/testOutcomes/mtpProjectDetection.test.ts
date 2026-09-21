import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { isMtpCapable } from '../../testOutcomes/mtpProjectDetection';

/** Covers {@link isMtpCapable}, the TypeScript port of the VS/Rider ad hoc MTP-capability scan (issue #715 plan §5.7). */
suite('isMtpCapable', () => {
  let dir: string;

  setup(() => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reqnroll-mtp-project-detection-tests-'));
  });

  teardown(() => {
    fs.rmSync(dir, { recursive: true, force: true });
  });

  function projectFile(xml: string, relativePath = path.join('Proj', 'Proj.csproj')): string {
    const file = path.join(dir, relativePath);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, xml, 'utf8');
    return file;
  }

  for (const property of [
    'EnableMSTestRunner',
    'EnableNUnitRunner',
    'UseMicrosoftTestingPlatformRunner',
    'IsTestingPlatformApplication',
  ]) {
    test(`is true when the project itself sets ${property}`, () => {
      const project = projectFile(
        `<Project><PropertyGroup><${property}>true</${property}></PropertyGroup></Project>`,
      );

      assert.strictEqual(isMtpCapable(project), true);
    });
  }

  test('is false for a plain project', () => {
    const project = projectFile(
      '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
    );

    assert.strictEqual(isMtpCapable(project), false);
  });

  test('is false when the property is explicitly false', () => {
    const project = projectFile(
      '<Project><PropertyGroup><EnableMSTestRunner>false</EnableMSTestRunner></PropertyGroup></Project>',
    );

    assert.strictEqual(isMtpCapable(project), false);
  });

  test('finds a repo-root Directory.Build.props the project itself does not set', () => {
    fs.mkdirSync(path.join(dir, '.git'));
    fs.writeFileSync(
      path.join(dir, 'Directory.Build.props'),
      '<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>',
      'utf8',
    );
    const project = projectFile(
      '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
      path.join('src', 'Tests', 'Tests.csproj'),
    );

    assert.strictEqual(isMtpCapable(project), true);
  });

  test('does not climb above the git root', () => {
    const outside = path.join(dir, 'outside');
    fs.mkdirSync(outside, { recursive: true });
    fs.writeFileSync(
      path.join(outside, 'Directory.Build.props'),
      '<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>',
      'utf8',
    );
    const repo = path.join(outside, 'repo');
    fs.mkdirSync(path.join(repo, '.git'), { recursive: true });
    const project = projectFile(
      '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
      path.join('outside', 'repo', 'src', 'Tests', 'Tests.csproj'),
    );

    assert.strictEqual(isMtpCapable(project), false);
  });

  test('is false for a project file that does not exist and no props anywhere', () => {
    assert.strictEqual(isMtpCapable(path.join(dir, 'Missing', 'Missing.csproj')), false);
  });
});
