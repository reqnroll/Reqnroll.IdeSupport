import * as assert from 'assert';
import * as path from 'path';
import {
  buildOutputPath,
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

    test('returns an empty array when there are no items of any type', () => {
      assert.deepStrictEqual(toProjectFileItems({}), []);
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
