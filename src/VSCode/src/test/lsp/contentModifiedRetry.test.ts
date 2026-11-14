import * as assert from 'assert';
import { ResponseError } from 'vscode-languageclient/node';
import {
  CONTENT_MODIFIED_CODE,
  isContentModified,
  sendWithContentModifiedRetry,
} from '../../lsp/contentModifiedRetry';

const contentModified = () => new ResponseError(CONTENT_MODIFIED_CODE, 'content modified');

suite('contentModifiedRetry', () => {
  test('isContentModified recognises only a ResponseError with the ContentModified code', () => {
    assert.strictEqual(isContentModified(contentModified()), true);
    assert.strictEqual(isContentModified(new ResponseError(-32603, 'x')), false);
    assert.strictEqual(isContentModified(new Error('content modified')), false);
  });

  test('resolves a null answer without retrying', async () => {
    let calls = 0;
    const result = await sendWithContentModifiedRetry(() => {
      calls++;
      return Promise.resolve(null);
    });
    assert.strictEqual(result, null);
    assert.strictEqual(calls, 1);
  });

  test('resolves an empty answer without retrying', async () => {
    let calls = 0;
    const result = await sendWithContentModifiedRetry(() => {
      calls++;
      return Promise.resolve([] as string[]);
    });
    assert.deepStrictEqual(result, []);
    assert.strictEqual(calls, 1);
  });

  test('retries ContentModified until it succeeds', async () => {
    let calls = 0;
    const result = await sendWithContentModifiedRetry(
      () => (++calls < 3 ? Promise.reject(contentModified()) : Promise.resolve('ok')),
      { delayMs: 1 },
    );
    assert.strictEqual(result, 'ok');
    assert.strictEqual(calls, 3);
  });

  test('gives up after the bounded number of retries and rethrows', async () => {
    let calls = 0;
    await assert.rejects(
      sendWithContentModifiedRetry(
        () => {
          calls++;
          return Promise.reject(contentModified());
        },
        { retries: 3, delayMs: 1 },
      ),
      (err: unknown) => isContentModified(err),
    );
    assert.strictEqual(calls, 4);
  });

  test('does not retry other ResponseErrors', async () => {
    let calls = 0;
    await assert.rejects(
      sendWithContentModifiedRetry(() => {
        calls++;
        return Promise.reject(new ResponseError(-32603, 'internal'));
      }),
      /internal/,
    );
    assert.strictEqual(calls, 1);
  });

  test('does not retry a timeout or a closed connection', async () => {
    let calls = 0;
    await assert.rejects(
      sendWithContentModifiedRetry(() => {
        calls++;
        return Promise.reject(new Error('Connection is closed.'));
      }),
      /closed/,
    );
    assert.strictEqual(calls, 1);
  });
});
