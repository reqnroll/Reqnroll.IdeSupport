import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import {
  CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE,
  enumerateProjectFiles,
  tryEnableForWorkspace,
  writeTargetsFile,
} from '../../testOutcomes/mtpEphemeralInjection';

suite('mtpEphemeralInjection', () => {
  let dir: string;

  setup(() => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reqnroll-mtp-ephemeral-injection-tests-'));
  });

  teardown(() => {
    fs.rmSync(dir, { recursive: true, force: true });
  });

  // ── enumerateProjectFiles ──────────────────────────────────────────────

  suite('enumerateProjectFiles', () => {
    test('finds .csproj files at any depth', () => {
      fs.mkdirSync(path.join(dir, 'src', 'Nested'), { recursive: true });
      fs.writeFileSync(path.join(dir, 'Root.csproj'), '', 'utf8');
      fs.writeFileSync(path.join(dir, 'src', 'Nested', 'Nested.csproj'), '', 'utf8');

      const found = enumerateProjectFiles(dir)
        .map((f) => path.basename(f))
        .sort();

      assert.deepStrictEqual(found, ['Nested.csproj', 'Root.csproj']);
    });

    for (const excluded of ['bin', 'obj', '.git', '.vs', '.vscode', 'node_modules']) {
      test(`prunes the excluded directory '${excluded}'`, () => {
        fs.mkdirSync(path.join(dir, excluded), { recursive: true });
        fs.writeFileSync(path.join(dir, excluded, 'Inside.csproj'), '', 'utf8');
        fs.writeFileSync(path.join(dir, 'Root.csproj'), '', 'utf8');

        const found = enumerateProjectFiles(dir).map((f) => path.basename(f));

        assert.deepStrictEqual(found, ['Root.csproj']);
      });
    }

    test('returns nothing for a directory with no .csproj files', () => {
      assert.deepStrictEqual(enumerateProjectFiles(dir), []);
    });
  });

  // ── writeTargetsFile ────────────────────────────────────────────────────

  suite('writeTargetsFile', () => {
    test('declares the HintPath reference and the builder hook', () => {
      const file = writeTargetsFile(
        path.join('C:', 'ext', 'MtpReporter', 'Reqnroll.IdeSupport.TestReporter.MTP.dll'),
      );
      try {
        const xml = fs.readFileSync(file, 'utf8');
        assert.match(xml, /<Reference Include="Reqnroll\.IdeSupport\.TestReporter\.MTP">/);
        assert.match(xml, /<HintPath>.*Reqnroll\.IdeSupport\.TestReporter\.MTP\.dll<\/HintPath>/);
        assert.match(
          xml,
          /<TestingPlatformBuilderHook Include="a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c">/,
        );
        assert.match(
          xml,
          /<TypeFullName>Reqnroll\.IdeSupport\.TestReporter\.MTP\.TestingPlatformBuilderHook<\/TypeFullName>/,
        );
        assert.doesNotMatch(xml, /<Import /);
      } finally {
        fs.rmSync(path.dirname(file), { recursive: true, force: true });
      }
    });

    test('chain-imports a pre-existing CustomAfterMicrosoftCommonTargets value', () => {
      const file = writeTargetsFile(
        path.join('C:', 'ext', 'Reqnroll.IdeSupport.TestReporter.MTP.dll'),
        path.join('C:', 'other', 'SomeOther.targets'),
      );
      try {
        const xml = fs.readFileSync(file, 'utf8');
        assert.match(
          xml,
          /<Import Project=".*SomeOther\.targets" Condition="Exists\('.*SomeOther\.targets'\)" \/>/,
        );
      } finally {
        fs.rmSync(path.dirname(file), { recursive: true, force: true });
      }
    });
  });

  // ── tryEnableForWorkspace ───────────────────────────────────────────────

  suite('tryEnableForWorkspace', () => {
    let original: string | undefined;

    setup(() => {
      original = process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE];
      delete process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE];
    });

    teardown(() => {
      if (original === undefined) {
        delete process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE];
      } else {
        process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE] = original;
      }
    });

    test('sets the environment variable when an MTP-capable project exists', () => {
      fs.writeFileSync(
        path.join(dir, 'Tests.csproj'),
        '<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>',
        'utf8',
      );
      const reporterDllPath = path.join(dir, 'Reqnroll.IdeSupport.TestReporter.MTP.dll');
      fs.writeFileSync(reporterDllPath, 'not a real assembly', 'utf8');

      const result = tryEnableForWorkspace([dir], reporterDllPath);

      assert.strictEqual(result, true);
      const value = process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE];
      assert.ok(value);
      assert.strictEqual(fs.existsSync(value), true);
    });

    test('does not set the environment variable when no project is MTP-capable', () => {
      fs.writeFileSync(
        path.join(dir, 'Tests.csproj'),
        '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>',
        'utf8',
      );
      const reporterDllPath = path.join(dir, 'Reqnroll.IdeSupport.TestReporter.MTP.dll');
      fs.writeFileSync(reporterDllPath, 'not a real assembly', 'utf8');

      const result = tryEnableForWorkspace([dir], reporterDllPath);

      assert.strictEqual(result, false);
      assert.strictEqual(process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE], undefined);
    });

    test('does not set the environment variable when the reporter dll is missing', () => {
      fs.writeFileSync(
        path.join(dir, 'Tests.csproj'),
        '<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>',
        'utf8',
      );

      const result = tryEnableForWorkspace([dir], path.join(dir, 'Missing.dll'));

      assert.strictEqual(result, false);
      assert.strictEqual(process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE], undefined);
    });
  });
});
