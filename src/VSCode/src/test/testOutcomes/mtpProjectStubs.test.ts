import * as assert from 'assert';
import * as fs from 'fs';
import nodeFs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  activateMtpProjectStubs,
  buildStubXml,
  enumerateProjectFiles,
  detectReqnrollUsage,
  projectsForAssetsFile,
  removeStub,
  resolveProjectExtensionsDirectory,
  syncStub,
  syncStubsForProjects,
  syncStubsForWorkspace,
  writeStub,
} from '../../testOutcomes/mtpProjectStubs';

/**
 * Records every `fs.readdirSync` call (still delegating to the real one) until `restore` — the
 * blocking call issue #998 keeps off the extension host's event loop during activation.
 */
function recordSyncReaddirs(): { calls: string[]; restore: () => void } {
  const target = nodeFs as unknown as { readdirSync: (...args: unknown[]) => unknown };
  const original = target.readdirSync;
  const calls: string[] = [];
  target.readdirSync = (...args: unknown[]) => {
    calls.push(String(args[0]));
    return original(...args);
  };
  return {
    calls,
    restore: () => {
      target.readdirSync = original;
    },
  };
}

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
    test('finds .csproj files at any depth', async () => {
      project('Root.csproj');
      project(path.join('src', 'Nested', 'Nested.csproj'));

      const found = (await enumerateProjectFiles(dir)).map((f) => path.basename(f)).sort();

      assert.deepStrictEqual(found, ['Nested.csproj', 'Root.csproj']);
    });

    for (const excluded of ['bin', 'obj', '.git', '.vs', '.vscode', 'node_modules']) {
      test(`prunes the excluded directory '${excluded}'`, async () => {
        project(path.join(excluded, 'Inside.csproj'));
        project('Root.csproj');

        assert.deepStrictEqual(
          (await enumerateProjectFiles(dir)).map((f) => path.basename(f)),
          ['Root.csproj'],
        );
      });
    }

    test('walks the tree without a blocking readdirSync (issue #998)', async () => {
      project('Root.csproj');
      project(path.join('src', 'Nested', 'Nested.csproj'));

      const recorder = recordSyncReaddirs();
      let found: string[];
      try {
        found = await enumerateProjectFiles(dir);
      } finally {
        recorder.restore();
      }

      assert.deepStrictEqual(found.map((f) => path.basename(f)).sort(), [
        'Nested.csproj',
        'Root.csproj',
      ]);
      assert.deepStrictEqual(recorder.calls, [], 'the workspace walk must be asynchronous');
    });
  });

  // ── activateMtpProjectStubs ─────────────────────────────────────────────

  test('activation with the feature disabled returns before walking the workspace (issue #998)', async () => {
    assert.ok(
      (vscode.workspace.workspaceFolders ?? []).length > 0,
      'the test host opens a workspace folder',
    );
    assert.strictEqual(
      vscode.workspace.getConfiguration('reqnroll').get<boolean>('testOutcomes.enabled', false),
      false,
      'the feature is off by default',
    );
    const context = {
      extensionMode: vscode.ExtensionMode.Test,
      extensionPath: dir,
      subscriptions: [],
    };

    const recorder = recordSyncReaddirs();
    let pending: Promise<void>;
    try {
      pending = activateMtpProjectStubs(context);
    } finally {
      recorder.restore();
    }

    assert.deepStrictEqual(
      recorder.calls,
      [],
      'no synchronous directory read may run on the activation path',
    );
    await pending;
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

  // ── Reqnroll projects only ─────────────────────────────────────────────

  /** A trimmed project.assets.json whose restore graph is `libraries` ("Name/Version"). */
  function assets(...libraries: string[]): string {
    return JSON.stringify(
      {
        version: 3,
        targets: { 'net10.0': {} },
        libraries: Object.fromEntries(
          libraries.map((l) => [l, { type: 'package', path: l.toLowerCase() }]),
        ),
        packageFolders: { 'C:\\Users\\me\\.nuget\\packages\\': {} },
        project: {
          restore: { projectName: 'App', projectPath: 'C:\\src\\ReqnrollDemo\\App.csproj' },
        },
        logs: [{ code: 'NU1603', message: 'Reqnroll/3.3.4' }],
      },
      null,
      2,
    );
  }

  function restoredProject(relativePath: string, ...libraries: string[]): string {
    const csproj = project(relativePath);
    const obj = path.join(path.dirname(csproj), 'obj');
    fs.mkdirSync(obj, { recursive: true });
    fs.writeFileSync(path.join(obj, 'project.assets.json'), assets(...libraries), 'utf8');
    return csproj;
  }

  const capable = (): Promise<boolean> => Promise.resolve(true);

  const stubOf = (csproj: string): string =>
    path.join(path.dirname(csproj), 'obj', path.basename(csproj) + '.reqnroll-ide.targets');

  suite('detectReqnrollUsage', () => {
    for (const library of [
      'Reqnroll.xunit.v3/3.3.3', // a runner plugin, referenced directly
      'Reqnroll/3.3.4', // the runtime alone, e.g. arriving through an in-house meta-package
      'MyCompany.ReqnrollSteps/1.0.0', // a third-party package or project reference with the name in it
      'reqnroll.mstest/3.3.4', // case-insensitive, like the LSP server's rule
    ]) {
      test(`finds '${library}' in the restore graph`, () => {
        assert.strictEqual(
          detectReqnrollUsage(
            assets('MSTest.TestAdapter/4.2.3', library, 'System.Text.Json/9.0.0'),
          ),
          true,
        );
      });
    }

    test('is false without Reqnroll even when paths and messages mention it, and unknown before restore', () => {
      assert.strictEqual(
        detectReqnrollUsage(assets('MSTest.TestAdapter/4.2.3', 'Microsoft.Testing.Platform/2.2.3')),
        false,
      );
      assert.strictEqual(detectReqnrollUsage(undefined), undefined);
    });
  });

  suite('syncStub', () => {
    test('writes a stub for a restored Reqnroll project', async () => {
      const csproj = restoredProject(path.join('App', 'App.csproj'), 'Reqnroll.MsTest/3.3.4');

      assert.strictEqual(
        await syncStub(csproj, bundleTargets, capable, readOrUndefined, noEvaluation),
        'written',
      );
      assert.strictEqual(fs.readFileSync(stubOf(csproj), 'utf8'), buildStubXml(bundleTargets));
    });

    test('writes nothing into a project that does not use Reqnroll and removes an earlier stub', async () => {
      const csproj = restoredProject(path.join('App', 'App.csproj'), 'MSTest.TestAdapter/4.2.3');
      fs.writeFileSync(stubOf(csproj), buildStubXml(bundleTargets), 'utf8');
      const nuGetTargets = path.join(path.dirname(csproj), 'obj', 'App.csproj.nuget.g.targets');
      fs.writeFileSync(nuGetTargets, '<Project />', 'utf8');

      assert.strictEqual(
        await syncStub(csproj, bundleTargets, capable, readOrUndefined, noEvaluation),
        'notReqnroll',
      );
      assert.strictEqual(fs.existsSync(stubOf(csproj)), false);
      assert.strictEqual(fs.existsSync(nuGetTargets), true, 'only our own stub is ever removed');
    });

    test('leaves a project that has not been restored yet alone', async () => {
      const csproj = project(path.join('App', 'App.csproj'));

      assert.strictEqual(
        await syncStub(csproj, bundleTargets, capable, readOrUndefined, noEvaluation),
        'notRestored',
      );
      assert.strictEqual(fs.existsSync(path.join(dir, 'App', 'obj')), false);
    });
  });

  suite('syncStubsForWorkspace', () => {
    test('writes stubs only into MTP-capable projects that use Reqnroll', async () => {
      const bundle = path.join(dir, 'bundle', 'Reqnroll.IdeSupport.TestReporter.MTP.targets');
      fs.mkdirSync(path.dirname(bundle), { recursive: true });
      fs.writeFileSync(bundle, '<Project />');
      const specs = restoredProject(path.join('Specs', 'Specs.csproj'), 'Reqnroll.xunit.v3/3.3.3');
      const unitTests = restoredProject(
        path.join('UnitTests', 'UnitTests.csproj'),
        'xunit.v3.mtp-v2/4.0.1',
      );
      const lib = restoredProject(path.join('Lib', 'Lib.csproj'), 'Reqnroll/3.3.4');

      const checkedForMtp: string[] = [];

      const count = await syncStubsForWorkspace([dir], bundle, (p) => {
        checkedForMtp.push(p);
        return Promise.resolve(p !== lib);
      });

      assert.strictEqual(count, 1);
      assert.deepStrictEqual(
        checkedForMtp.sort(),
        [lib, specs].sort(),
        'the MTP-capability check (which can run dotnet msbuild) is only paid for Reqnroll projects',
      );
      assert.strictEqual(fs.existsSync(stubOf(specs)), true);
      assert.strictEqual(fs.existsSync(stubOf(unitTests)), false, 'MTP-capable, but no Reqnroll');
      assert.strictEqual(fs.existsSync(stubOf(lib)), false, 'uses Reqnroll, but not MTP-capable');
    });

    test('bounds how many projects are synced (and may spawn dotnet msbuild) at once (issue #998)', async () => {
      const bundle = path.join(dir, 'bundle', 'Reqnroll.IdeSupport.TestReporter.MTP.targets');
      fs.mkdirSync(path.dirname(bundle), { recursive: true });
      fs.writeFileSync(bundle, '<Project />');
      const projects = Array.from({ length: 12 }, (_, i) =>
        restoredProject(path.join(`Specs${i}`, `Specs${i}.csproj`), 'Reqnroll.xunit.v3/3.3.3'),
      );

      let inFlight = 0;
      let maxInFlight = 0;
      const checked: string[] = [];
      const count = await syncStubsForProjects(projects, bundle, async (p) => {
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        await new Promise((resolve) => setTimeout(resolve, 10));
        inFlight--;
        checked.push(p);
        return true;
      });

      assert.strictEqual(count, 12, 'every project is still synced');
      assert.deepStrictEqual(checked.sort(), [...projects].sort());
      assert.ok(maxInFlight > 1, `projects are still synced in parallel (max ${maxInFlight})`);
      assert.ok(maxInFlight <= 4, `at most 4 projects in flight, saw ${maxInFlight}`);
    });

    test('writes nothing when the bundle is missing', async () => {
      const specs = restoredProject(path.join('Specs', 'Specs.csproj'), 'Reqnroll.xunit.v3/3.3.3');

      assert.strictEqual(
        await syncStubsForWorkspace([dir], path.join(dir, 'missing.targets'), () =>
          Promise.resolve(true),
        ),
        0,
      );
      assert.strictEqual(fs.existsSync(stubOf(specs)), false);
    });
  });

  test('projectsForAssetsFile maps obj/project.assets.json back to the C# projects beside obj/', () => {
    const csproj = restoredProject(path.join('App', 'App.csproj'), 'Reqnroll/3.3.4');
    project(path.join('App', 'App.vbproj'));

    assert.deepStrictEqual(
      projectsForAssetsFile(path.join(dir, 'App', 'obj', 'project.assets.json')),
      [csproj],
    );
  });
});
