import * as assert from 'assert';
import {
  MAX_BUFFER_OVERFLOW_CODE,
  MSBUILD_MAX_BUFFER_BYTES,
  isMaxBufferOverflowError,
} from '../../util/msbuildProcess';

/** Covers the shared msbuild process seam (issue #1008). */
suite('msbuildProcess', () => {
  test('raises the msbuild output buffer well above the old 1 MB limit', () => {
    assert.ok(
      MSBUILD_MAX_BUFFER_BYTES >= 16 * 1024 * 1024,
      `expected the buffer to be raised from 1 MB, got ${MSBUILD_MAX_BUFFER_BYTES} bytes`,
    );
  });

  test('recognises Node maxBuffer-overflow errors', () => {
    const overflow = Object.assign(new Error('stdout maxBuffer length exceeded'), {
      code: MAX_BUFFER_OVERFLOW_CODE,
    });

    assert.strictEqual(isMaxBufferOverflowError(overflow), true);
  });

  test('does not treat other errors, or nothing at all, as a buffer overflow', () => {
    assert.strictEqual(isMaxBufferOverflowError(new Error('boom')), false);
    assert.strictEqual(isMaxBufferOverflowError(undefined), false);
    assert.strictEqual(isMaxBufferOverflowError(null), false);
  });
});
