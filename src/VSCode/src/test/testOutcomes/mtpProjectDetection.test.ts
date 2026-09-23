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

  /** Fails the test if invoked — used where the MSBuild fallback must not be reached. */
  function unreachableEvaluator(): Promise<boolean | null> {
    assert.fail('evaluateMtp should not have been invoked');
  }

  for (const property of [
    'EnableMSTestRunner',
    'EnableNUnitRunner',
    'UseMicrosoftTestingPlatformRunner',
    'IsTestingPlatformApplication',
  ]) {
    test(`is true when the project itself sets ${property}`, async () => {
      const project = projectFile(
        `<Project><PropertyGroup><${property}>true</${property}></PropertyGroup></Project>`,
      );

      assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), true);
    });
  }

  test('is false for a plain project', async () => {
    const project = projectFile(
      '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
    );

    assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), false);
  });

  test('is false when the property is explicitly false', async () => {
    const project = projectFile(
      '<Project><PropertyGroup><EnableMSTestRunner>false</EnableMSTestRunner></PropertyGroup></Project>',
    );

    assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), false);
  });

  test('finds a repo-root Directory.Build.props the project itself does not set', async () => {
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

    assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), true);
  });

  test('does not climb above the git root', async () => {
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

    assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), false);
  });

  test('is false for a project file that does not exist and no props anywhere', async () => {
    assert.strictEqual(
      await isMtpCapable(
        path.join(dir, 'Missing', 'Missing.csproj'),
        undefined,
        unreachableEvaluator,
      ),
      false,
    );
  });

  // ── MSBuild-evaluation fallback (issue #722: imported-props-file gap) ────────────────────────

  suite('MSBuild evaluation fallback', () => {
    function testSdkProjectFile(): string {
      return projectFile(
        '<Project><ItemGroup>' +
          '<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />' +
          '</ItemGroup></Project>',
      );
    }

    test('trusts a true MSBuild evaluation for a test project the text scan missed', async () => {
      // A project made MTP-capable only through an imported props file (e.g. the full xunit.v3
      // runner package pulling in Microsoft.Testing.Platform.MSBuild) has none of the marker
      // properties as literal text anywhere a scan would look, but a real MSBuild evaluation sees it.
      const project = testSdkProjectFile();

      assert.strictEqual(await isMtpCapable(project, undefined, () => Promise.resolve(true)), true);
    });

    test('does not invoke MSBuild evaluation for a project that is not a test project', async () => {
      // Gated on "looks like a test project" so a solution-wide scan doesn't shell out to
      // `dotnet msbuild` for every ordinary non-test project.
      const project = projectFile(
        '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
      );

      assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), false);
    });

    test('is false when the MSBuild evaluation returns false for a test project', async () => {
      const project = testSdkProjectFile();

      assert.strictEqual(
        await isMtpCapable(project, undefined, () => Promise.resolve(false)),
        false,
      );
    });

    test('is false when the MSBuild evaluation is unavailable for a test project', async () => {
      const project = testSdkProjectFile();

      assert.strictEqual(
        await isMtpCapable(project, undefined, () => Promise.resolve(null)),
        false,
      );
    });

    test('does not invoke MSBuild evaluation when the text scan already found a match', async () => {
      const project = projectFile(
        '<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>' +
          '<ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />' +
          '</ItemGroup></Project>',
      );

      assert.strictEqual(await isMtpCapable(project, undefined, unreachableEvaluator), true);
    });
  });
});
