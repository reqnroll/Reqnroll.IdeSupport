import * as assert from 'assert';
import { buildTestFilter } from '../../testRunner/testFilterBuilder';
import { ScenarioTestTargetDto } from '../../testRunner/scenarioTestTarget';

function target(overrides: Partial<ScenarioTestTargetDto> = {}): ScenarioTestTargetDto {
  return {
    declaringTypeFullName: 'Tests.FFeature',
    methodName: 'AddNumbers',
    isParameterized: false,
    rowArguments: null,
    rowIndex: null,
    ...overrides,
  };
}

suite('testFilterBuilder', () => {
  suite('buildTestFilter', () => {
    test('a single non-parameterized target produces one term', () => {
      const filter = buildTestFilter([target()]);
      assert.strictEqual(filter, 'FullyQualifiedName=Tests.FFeature.AddNumbers');
    });

    test('row-tests targets sharing one method name collapse to a single term', () => {
      const targets = [
        target({ isParameterized: true, rowIndex: 0 }),
        target({ isParameterized: true, rowIndex: 1 }),
        target({ isParameterized: true, rowIndex: 2 }),
      ];

      const filter = buildTestFilter(targets);

      assert.strictEqual(filter, 'FullyQualifiedName=Tests.FFeature.AddNumbers');
    });

    test('individual-methods targets with distinct method names each get their own term', () => {
      const targets = [
        target({ methodName: 'CheckValue_1' }),
        target({ methodName: 'CheckValue_2' }),
        target({ methodName: 'CheckValue_Extra_3' }),
      ];

      const filter = buildTestFilter(targets);

      assert.strictEqual(
        filter,
        'FullyQualifiedName=Tests.FFeature.CheckValue_1|' +
          'FullyQualifiedName=Tests.FFeature.CheckValue_2|' +
          'FullyQualifiedName=Tests.FFeature.CheckValue_Extra_3',
      );
    });

    test('an empty target list produces an empty filter', () => {
      assert.strictEqual(buildTestFilter([]), '');
    });

    test('distinct declaring types with the same method name are not conflated', () => {
      const targets = [
        target({ declaringTypeFullName: 'Tests.FFeature' }),
        target({ declaringTypeFullName: 'Tests.OtherFeature' }),
      ];

      const filter = buildTestFilter(targets);

      assert.strictEqual(
        filter,
        'FullyQualifiedName=Tests.FFeature.AddNumbers|FullyQualifiedName=Tests.OtherFeature.AddNumbers',
      );
    });
  });
});
