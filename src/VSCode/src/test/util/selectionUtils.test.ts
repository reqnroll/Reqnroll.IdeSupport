import * as assert from 'assert';
import { normalizeSelectionLines } from '../../util/selectionUtils';

suite('normalizeSelectionLines', () => {
  test('single-line selection is unchanged', () => {
    const [start, end] = normalizeSelectionLines(3, 3, 0);
    assert.strictEqual(start, 3);
    assert.strictEqual(end, 3);
  });

  test('multi-line selection with non-zero end character is unchanged', () => {
    const [start, end] = normalizeSelectionLines(1, 4, 5);
    assert.strictEqual(start, 1);
    assert.strictEqual(end, 4);
  });

  test('multi-line selection ending at col 0 reduces endLine by one', () => {
    // Cursor landed at the start of line 4 without selecting any content on it
    const [start, end] = normalizeSelectionLines(1, 4, 0);
    assert.strictEqual(start, 1);
    assert.strictEqual(end, 3);
  });

  test('two-line selection ending at col 0 collapses to single line', () => {
    const [start, end] = normalizeSelectionLines(2, 3, 0);
    assert.strictEqual(start, 2);
    assert.strictEqual(end, 2);
  });
});
