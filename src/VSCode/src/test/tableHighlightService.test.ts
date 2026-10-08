import * as assert from 'assert';
import { getPipeIndexes, getTrimmedCellRange, isTableRow } from '../tableHighlightService';

suite('tableHighlightService', () => {
  suite('isTableRow', () => {
    test('recognizes a well-formed table row', () => {
      assert.strictEqual(isTableRow('\t\t| operand | type |'), true);
    });

    test('rejects a line with no pipes', () => {
      assert.strictEqual(isTableRow('\t\tGiven the operands entered'), false);
    });

    test('rejects a line with only one pipe', () => {
      assert.strictEqual(isTableRow('\t\t| operand'), false);
    });

    test('rejects a line where the first non-whitespace character is not a pipe', () => {
      assert.strictEqual(isTableRow('\t\tfoo | bar |'), false);
    });

    test('does not count an escaped pipe towards the two-separator minimum', () => {
      // '| a \| b' has a leading pipe and an escaped pipe, so it has only one column separator.
      assert.strictEqual(isTableRow('| a \\| b'), false);
    });
  });

  suite('getPipeIndexes', () => {
    test('returns the index of every pipe character', () => {
      assert.deepStrictEqual(getPipeIndexes('| a | b |'), [0, 4, 8]);
    });

    test('returns an empty array when there are no pipes', () => {
      assert.deepStrictEqual(getPipeIndexes('no pipes here'), []);
    });

    test('ignores a pipe escaped by a single backslash', () => {
      // '| a \| b |' — the pipes at 0 and 9 are column separators; the pipe at 5 is cell content.
      assert.deepStrictEqual(getPipeIndexes('| a \\| b |'), [0, 9]);
    });

    test('counts a pipe preceded by an escaped backslash as a separator', () => {
      // '| a \\| b |' — the two backslashes are one escaped backslash, so the pipe at 6 is real.
      assert.deepStrictEqual(getPipeIndexes('| a \\\\| b |'), [0, 6, 10]);
    });

    test('counts a pipe after an even run of backslashes as a separator', () => {
      // Four backslashes collapse to two escaped backslashes, so the pipe at 8 is real.
      assert.deepStrictEqual(getPipeIndexes('| a \\\\\\\\| b |'), [0, 8, 12]);
    });

    test('ignores a pipe after an odd run of backslashes', () => {
      // Three backslashes are an escaped backslash plus one escaping backslash, so the pipe at 7
      // is cell content.
      assert.deepStrictEqual(getPipeIndexes('| a \\\\\\| b |'), [0, 11]);
    });
  });

  suite('getTrimmedCellRange', () => {
    test('trims leading and trailing whitespace from the cell content', () => {
      const range = getTrimmedCellRange(0, '|  operand  |', 0, 12);

      assert.ok(range);
      assert.strictEqual(range.start.character, 3);
      assert.strictEqual(range.end.character, 10);
    });

    test('returns undefined for an empty cell', () => {
      const range = getTrimmedCellRange(0, '|    |', 0, 5);

      assert.strictEqual(range, undefined);
    });

    test('returns undefined for a whitespace-only cell', () => {
      const range = getTrimmedCellRange(0, '|   |', 0, 4);

      assert.strictEqual(range, undefined);
    });
  });
});
