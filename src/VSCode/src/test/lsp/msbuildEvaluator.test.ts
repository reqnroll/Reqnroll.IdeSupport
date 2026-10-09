import * as assert from 'assert';
import * as path from 'path';
import {
  buildOutputPath,
  evaluateProject,
  findTargetKey,
  readPackageReferences,
  tfmToShort,
  toProjectFileItems,
} from '../../lsp/msbuildEvaluator';

suite('msbuildEvaluator', () => {
  suite('tfmToShort', () => {
    test('converts .NETFramework monikers, appending the patch digit only when non-zero', () => {
      assert.strictEqual(tfmToShort('.NETFramework,Version=v4.8'), 'net48');
      assert.strictEqual(tfmToShort('.NETFramework,Version=v4.8.1'), 'net481');
      assert.strictEqual(tfmToShort('.NETFramework,Version=v4.5'), 'net45');
    });

    test('converts .NETStandard monikers', () => {
      assert.strictEqual(tfmToShort('.NETStandard,Version=v2.0'), 'netstandard2.0');
      assert.strictEqual(tfmToShort('.NETStandard,Version=v2.1'), 'netstandard2.1');
    });

    test('converts .NETCoreApp monikers', () => {
      assert.strictEqual(tfmToShort('.NETCoreApp,Version=v8.0'), 'net8.0');
      assert.strictEqual(tfmToShort('.NETCoreApp,Version=v10.0'), 'net10.0');
    });

    test('falls back to a sanitized lowercase string for an unrecognized moniker', () => {
      assert.strictEqual(tfmToShort('Some Weird TFM!'), 'someweirdtfm');
    });
  });

  suite('findTargetKey', () => {
    test('returns the short-TFM key when it exists in the assets file', () => {
      const assets = { targets: { 'net8.0': {}, 'netstandard2.0': {} } };
      assert.strictEqual(findTargetKey(assets, '.NETCoreApp,Version=v8.0'), 'net8.0');
    });

    test('falls back to the first available target when the short TFM has no matching key', () => {
      const assets = { targets: { net481: {} } };
      assert.strictEqual(findTargetKey(assets, '.NETCoreApp,Version=v8.0'), 'net481');
    });

    test('returns undefined when the assets file has no targets at all', () => {
      assert.strictEqual(findTargetKey({}, '.NETCoreApp,Version=v8.0'), undefined);
    });
  });

  suite('readPackageReferences', () => {
    test('returns an empty array when the assets file path is empty', () => {
      assert.deepStrictEqual(readPackageReferences('', '.NETCoreApp,Version=v8.0'), []);
    });

    test('returns an empty array when the assets file does not exist on disk', () => {
      assert.deepStrictEqual(
        readPackageReferences('Z:\\nonexistent\\project.assets.json', '.NETCoreApp,Version=v8.0'),
        [],
      );
    });
  });

  suite('toProjectFileItems', () => {
    test('classifies Compile items as bindings and None/Content items as features', () => {
      const items = {
        Compile: [{ Identity: 'Steps.cs', FullPath: 'C:\\proj\\Steps.cs' }],
        None: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
        Content: [{ Identity: 'B.feature', FullPath: 'C:\\proj\\B.feature' }],
      };

      const result = toProjectFileItems(items);

      assert.deepStrictEqual(result.map((r) => r.role).sort(), ['binding', 'feature', 'feature']);
    });

    test('ignores items whose extension does not match the item type', () => {
      const items = {
        Compile: [{ Identity: 'readme.txt', FullPath: 'C:\\proj\\readme.txt' }],
      };

      assert.deepStrictEqual(toProjectFileItems(items), []);
    });

    test('deduplicates the same resolved path appearing under more than one item type', () => {
      // A linked file can legitimately appear under both None and Content.
      const items = {
        None: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
        Content: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
      };

      assert.strictEqual(toProjectFileItems(items).length, 1);
    });

    test('classifies ReqnrollFeatureFiles .feature items as features', () => {
      // Reqnroll.Tools.MsBuild.Generation.props (pulled in transitively by Reqnroll.MsTest/
      // Reqnroll.xUnit/etc.) appends **/*.feature to $(DefaultItemExcludes), so real Reqnroll
      // projects never surface .feature files under None/Content — only via the private
      // ReqnrollFeatureFiles item that package's .props statically populates (confirmed against
      // an actual `dotnet msbuild -getItem` run against the Quickstart sample; EmbeddedResource
      // is NOT a viable substitute here — that package only adds .feature files to
      // EmbeddedResource inside a Target, which a bare -getItem evaluation never runs).
      const items = {
        Compile: [
          { Identity: 'A.feature.cs', FullPath: 'C:\\proj\\A.feature.cs' },
          { Identity: 'Steps.cs', FullPath: 'C:\\proj\\Steps.cs' },
        ],
        ReqnrollFeatureFiles: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
      };

      const result = toProjectFileItems(items);

      assert.deepStrictEqual(result.map((r) => r.role).sort(), ['binding', 'binding', 'feature']);
      assert.ok(
        result.some((r) => r.path === 'C:\\proj\\A.feature' && r.role === 'feature'),
        'expected the ReqnrollFeatureFiles item to be classified as a feature file',
      );
    });

    test('deduplicates a .feature file appearing under both ReqnrollFeatureFiles and None/Content', () => {
      const items = {
        None: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
        ReqnrollFeatureFiles: [{ Identity: 'A.feature', FullPath: 'C:\\proj\\A.feature' }],
      };

      assert.strictEqual(toProjectFileItems(items).length, 1);
    });

    test('returns no feature files when the project does not reference the Reqnroll MSBuild package', () => {
      // -getItem for an item type that's never defined for a given project (e.g. a plain .csproj
      // with no Reqnroll package reference) comes back as an empty array, not an error/omission.
      const items = {
        Compile: [{ Identity: 'Currency.cs', FullPath: 'C:\\proj\\Currency.cs' }],
        None: [],
        Content: [],
        ReqnrollFeatureFiles: [],
      };

      const result = toProjectFileItems(items);

      assert.deepStrictEqual(result, [{ path: 'C:\\proj\\Currency.cs', role: 'binding' }]);
    });

    test('returns an empty array when there are no items of any type', () => {
      assert.deepStrictEqual(toProjectFileItems({}), []);
    });
  });

  suite('evaluateProject', () => {
    const projectFile = path.resolve('repo', 'Multi', 'Multi.csproj');

    // Shapes mirror real `dotnet msbuild -getProperty/-getItem` output (SDK 10.0.401) for a
    // project with <TargetFrameworks>net8.0;net10.0</TargetFrameworks>: the outer (no
    // TargetFramework) evaluation has an empty TargetFrameworkMoniker/ProjectAssetsFile, a
    // TFM-less OutputPath and an empty Compile list; the inner evaluation has all of them.
    const innerEvaluation = (tfm: string, moniker: string) =>
      JSON.stringify({
        Properties: {
          TargetFrameworkMoniker: moniker,
          OutputPath: `bin\\Debug\\${tfm}\\`,
          AssemblyName: 'Multi',
          RootNamespace: 'Multi',
          ProjectAssetsFile: '',
          TargetFramework: tfm,
          TargetFrameworks: 'net8.0;net10.0',
        },
        Items: { Compile: [{ Identity: 'Steps.cs', FullPath: 'C:\\repo\\Multi\\Steps.cs' }] },
      });

    const outerEvaluation = (targetFrameworks: string) =>
      JSON.stringify({
        Properties: {
          TargetFrameworkMoniker: '',
          OutputPath: 'bin\\Debug\\',
          AssemblyName: 'Multi',
          RootNamespace: 'Multi',
          ProjectAssetsFile: '',
          TargetFramework: '',
          TargetFrameworks: targetFrameworks,
        },
        Items: { Compile: [] },
      });

    /** Records each invocation's args and answers outer/inner evaluations like real MSBuild. */
    const stubRunner = (targetFrameworks: string) => {
      const calls: string[][] = [];
      const run = (args: readonly string[]): Promise<string | null> => {
        calls.push([...args]);
        const tfArg = args.find((a) => a.startsWith('-p:TargetFramework='));
        if (!tfArg) return Promise.resolve(outerEvaluation(targetFrameworks));
        const tfm = tfArg.slice('-p:TargetFramework='.length);
        return Promise.resolve(
          innerEvaluation(
            tfm,
            tfm === 'net8.0' ? '.NETCoreApp,Version=v8.0' : '.NETCoreApp,Version=v10.0',
          ),
        );
      };
      return { calls, run };
    };

    test('single-targeted project is evaluated once, without a TargetFramework override', async () => {
      const calls: string[][] = [];
      const run = (args: readonly string[]): Promise<string | null> => {
        calls.push([...args]);
        return Promise.resolve(innerEvaluation('net8.0', '.NETCoreApp,Version=v8.0'));
      };

      const result = await evaluateProject(projectFile, run);

      assert.ok(result, 'expected a result for a single-targeted project');
      assert.strictEqual(result.targetFrameworkMoniker, '.NETCoreApp,Version=v8.0');
      assert.strictEqual(
        result.outputAssemblyPath,
        path.resolve('repo', 'Multi', 'bin', 'Debug', 'net8.0', 'Multi.dll'),
      );
      assert.strictEqual(calls.length, 1);
      assert.ok(!calls[0].some((a) => a.startsWith('-p:TargetFramework=')));
    });

    test('multi-targeted project is re-evaluated for the first listed TargetFramework', async () => {
      const { calls, run } = stubRunner('net8.0;net10.0');

      const result = await evaluateProject(projectFile, run);

      assert.ok(result, 'expected a result for a multi-targeted project');
      assert.strictEqual(result.targetFrameworkMoniker, '.NETCoreApp,Version=v8.0');
      assert.strictEqual(
        result.outputAssemblyPath,
        path.resolve('repo', 'Multi', 'bin', 'Debug', 'net8.0', 'Multi.dll'),
      );
      assert.deepStrictEqual(result.files, [
        { path: 'C:\\repo\\Multi\\Steps.cs', role: 'binding' },
      ]);
      assert.strictEqual(calls.length, 2);
      assert.ok(calls[1].includes('-p:TargetFramework=net8.0'));
    });

    test('multi-target TFM choice ignores whitespace and empty entries in TargetFrameworks', async () => {
      const { calls, run } = stubRunner(' ; net10.0 ;net8.0;');

      const result = await evaluateProject(projectFile, run);

      assert.strictEqual(result?.targetFrameworkMoniker, '.NETCoreApp,Version=v10.0');
      assert.ok(calls[1].includes('-p:TargetFramework=net10.0'));
    });

    test('project with no TargetFramework information still evaluates to null', async () => {
      const { calls, run } = stubRunner('');

      assert.strictEqual(await evaluateProject(projectFile, run), null);
      assert.strictEqual(calls.length, 1);
    });

    test('returns null when the inner evaluation of a multi-targeted project fails', async () => {
      let call = 0;
      const run = () => Promise.resolve(call++ === 0 ? outerEvaluation('net8.0;net10.0') : null);

      assert.strictEqual(await evaluateProject(projectFile, run), null);
      assert.strictEqual(call, 2);
    });

    test('returns null when dotnet msbuild fails', async () => {
      assert.strictEqual(await evaluateProject(projectFile, () => Promise.resolve(null)), null);
    });
  });

  suite('buildOutputPath', () => {
    test('resolves OutputPath relative to the project directory and appends AssemblyName.dll', () => {
      // OutputPath always comes back backslash-delimited from MSBuild, even when evaluated on
      // a non-Windows host -- exercise that regardless of which OS this test itself runs on.
      const props = {
        TargetFrameworkMoniker: '.NETCoreApp,Version=v8.0',
        OutputPath: 'bin\\Debug\\net8.0\\',
        AssemblyName: 'MyProject',
        RootNamespace: 'MyProject',
        ProjectAssetsFile: '',
      };

      const projectFile = path.join('repo', 'MyProject', 'MyProject.csproj');
      const result = buildOutputPath(projectFile, props);

      const expected = path.resolve('repo', 'MyProject', 'bin', 'Debug', 'net8.0', 'MyProject.dll');
      assert.strictEqual(result, expected);
    });
  });
});
