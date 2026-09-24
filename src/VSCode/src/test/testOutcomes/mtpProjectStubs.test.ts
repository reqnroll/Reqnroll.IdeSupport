import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import {
  buildStubXml,
  enumerateProjectFiles,
  removeStub,
  resolveProjectExtensionsDirectory,
  writeStub,
  writeStubsForWorkspace,
} from '../../testOutcomes/mtpProjectStubs';

/**
 * Where and when VS Code writes the issue #741 project-local `obj/<Project>.csproj.reqnroll-ide.targets`
 * stub. What the stub's import does to a build is covered by `SourceInjectionBuildTests` in
 * Reqnroll.IdeSupport.TestReporter.MTP.Tests.
 */
suite('mtpProjectStubs', () => {
  const bundleTargets = path.join(
    'C:',
    'ext',
    'mtpreporter',
    'Reqnroll.IdeSupport.TestReporter.MTP.targets',
  );
  const noEvaluation = (): Promise<string | undefined> => {
    throw new Error('must not evaluate on the fast path');
  };
  const readOrUndefined = (p: string): string | undefined =>
    fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : undefined;
  let dir: string;

  setup(() => {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reqnroll-mtp-project-stubs-tests-'));
  });

  teardown(() => {
    fs.rmSync(dir, { recursive: true, force: true });
  });

  function project(relativePath: string, xml = '<Project Sdk="Microsoft.NET.Sdk" />'): string {
    const file = path.join(dir, relativePath);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, xml, 'utf8');
    return file;
  }

  // ── enumerateProjectFiles ──────────────────────────────────────────────

  suite('enumerateProjectFiles', () => {
    test('finds .csproj files at any depth', () => {
      project('Root.csproj');
      project(path.join('src', 'Nested', 'Nested.csproj'));

      const found = enumerateProjectFiles(dir)
        .map((f) => path.basename(f))
        .sort();

      assert.deepStrictEqual(found, ['Nested.csproj', 'Root.csproj']);
    });

    for (const excluded of ['bin', 'obj', '.git', '.vs', '.vscode', 'node_modules']) {
      test(`prunes the excluded directory '${excluded}'`, () => {
        project(path.join(excluded, 'Inside.csproj'));
        project('Root.csproj');

        assert.deepStrictEqual(
          enumerateProjectFiles(dir).map((f) => path.basename(f)),
          ['Root.csproj'],
        );
      });
    }
  });

  // ── buildStubXml ────────────────────────────────────────────────────────

  test('the stub is an Exists-guarded import of the bundle and nothing else', () => {
    const xml = buildStubXml(bundleTargets);

    assert.ok(
      xml.includes(
        `<_ReqnrollIdeMtpReporterBundle>${bundleTargets}</_ReqnrollIdeMtpReporterBundle>`,
      ),
    );
    assert.ok(
      xml.includes(
        `<Import Project="$(_ReqnrollIdeMtpReporterBundle)" Condition="Exists('$(_ReqnrollIdeMtpReporterBundle)')" />`,
      ),
    );
    assert.doesNotMatch(xml, /<Reference|TestingPlatformBuilderHook|<ItemGroup/);
  });

  test('the stub escapes a user-profile path that would otherwise break every build', () => {
    const xml = buildStubXml(
      "/home/O'Brien & $Co 100%;@x/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.targets",
    );

    assert.ok(
      xml.includes(
        "<_ReqnrollIdeMtpReporterBundle>/home/O'Brien &amp; %24Co 100%25%3B%40x/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.targets</_ReqnrollIdeMtpReporterBundle>",
      ),
    );
  });

  // ── resolveProjectExtensionsDirectory ───────────────────────────────────

  suite('resolveProjectExtensionsDirectory', () => {
    test('an ordinary project gets obj/ without an MSBuild evaluation', async () => {
      const csproj = project(path.join('App', 'App.csproj'));

      assert.strictEqual(
        await resolveProjectExtensionsDirectory(csproj, readOrUndefined, noEvaluation),
        path.join(dir, 'App', 'obj'),
      );
    });

    test('a Directory.Build.props that moves obj defers to MSBuild', async () => {
      fs.writeFileSync(
        path.join(dir, 'Directory.Build.props'),
        '<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>',
      );
      const csproj = project(path.join('src', 'App', 'App.csproj'));
      const evaluated = path.join(dir, 'artifacts', 'obj', 'App');

      assert.strictEqual(
        await resolveProjectExtensionsDirectory(csproj, readOrUndefined, () =>
          Promise.resolve(evaluated),
        ),
        evaluated,
      );
    });

    test('a failed evaluation means no directory rather than a guess', async () => {
      const csproj = project(
        path.join('App', 'App.csproj'),
        '<Project><PropertyGroup><BaseIntermediateOutputPath>x/</BaseIntermediateOutputPath></PropertyGroup></Project>',
      );

      assert.strictEqual(
        await resolveProjectExtensionsDirectory(csproj, readOrUndefined, () =>
          Promise.resolve(undefined),
        ),
        undefined,
      );
    });
  });

  // ── writeStub / removeStub ──────────────────────────────────────────────

  suite('writeStub', () => {
    test('writes obj/<Project>.csproj.reqnroll-ide.targets and leaves an up-to-date stub untouched', async () => {
      const csproj = project(path.join('App', 'App.csproj'));

      const stub = await writeStub(csproj, bundleTargets, readOrUndefined, noEvaluation);
      assert.strictEqual(stub, path.join(dir, 'App', 'obj', 'App.csproj.reqnroll-ide.targets'));
      assert.strictEqual(fs.readFileSync(stub, 'utf8'), buildStubXml(bundleTargets));

      const stamp = new Date(2020, 0, 1);
      fs.utimesSync(stub, stamp, stamp);
      await writeStub(csproj, bundleTargets, readOrUndefined, noEvaluation);
      assert.strictEqual(
        fs.statSync(stub).mtime.getTime(),
        stamp.getTime(),
        'an unchanged stub must keep its timestamp',
      );
    });

    test('refreshes a stub pointing at an older extension install', async () => {
      const csproj = project(path.join('App', 'App.csproj'));
      await writeStub(
        csproj,
        path.join('C:', 'old', 'Reqnroll.IdeSupport.TestReporter.MTP.targets'),
        readOrUndefined,
        noEvaluation,
      );

      const stub = await writeStub(csproj, bundleTargets, readOrUndefined, noEvaluation);

      assert.ok(fs.readFileSync(stub!, 'utf8').includes(bundleTargets));
    });

    test('skips non-C# projects', async () => {
      const vbproj = project(path.join('App', 'App.vbproj'));

      assert.strictEqual(
        await writeStub(vbproj, bundleTargets, readOrUndefined, noEvaluation),
        undefined,
      );
      assert.strictEqual(fs.existsSync(path.join(dir, 'App', 'obj')), false);
    });

    test('removeStub deletes a stub written earlier and reports nothing to remove afterwards', async () => {
      const csproj = project(path.join('App', 'App.csproj'));
      const stub = await writeStub(csproj, bundleTargets, readOrUndefined, noEvaluation);

      assert.strictEqual(await removeStub(csproj, readOrUndefined, noEvaluation), true);
      assert.strictEqual(fs.existsSync(stub!), false);
      assert.strictEqual(await removeStub(csproj, readOrUndefined, noEvaluation), false);
    });
  });

  // ── writeStubsForWorkspace ──────────────────────────────────────────────

  suite('writeStubsForWorkspace', () => {
    test('writes stubs only into MTP-capable projects', async () => {
      const bundle = path.join(dir, 'bundle', 'Reqnroll.IdeSupport.TestReporter.MTP.targets');
      fs.mkdirSync(path.dirname(bundle), { recursive: true });
      fs.writeFileSync(bundle, '<Project />');
      const mtp = project(path.join('Mtp', 'Mtp.csproj'));
      const plain = project(path.join('Lib', 'Lib.csproj'));

      const count = await writeStubsForWorkspace([dir], bundle, (p) => Promise.resolve(p === mtp));

      assert.strictEqual(count, 1);
      assert.strictEqual(
        fs.existsSync(path.join(path.dirname(mtp), 'obj', 'Mtp.csproj.reqnroll-ide.targets')),
        true,
      );
      assert.strictEqual(fs.existsSync(path.join(path.dirname(plain), 'obj')), false);
    });

    test('writes nothing when the bundle is missing', async () => {
      project(path.join('Mtp', 'Mtp.csproj'));

      assert.strictEqual(
        await writeStubsForWorkspace([dir], path.join(dir, 'missing.targets'), () =>
          Promise.resolve(true),
        ),
        0,
      );
      assert.strictEqual(fs.existsSync(path.join(dir, 'Mtp', 'obj')), false);
    });
  });
});
