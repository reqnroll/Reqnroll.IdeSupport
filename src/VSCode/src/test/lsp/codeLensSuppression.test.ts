import * as assert from 'assert';
import { createCodeLensSuppressionMiddleware } from '../../lsp/codeLensSuppression';

suite('codeLensSuppression', () => {
  test('provideCodeLenses returns an empty array without calling next', () => {
    const middleware = createCodeLensSuppressionMiddleware();
    let nextCalled = false;
    const next = () => {
      nextCalled = true;
      return [];
    };

    const result = middleware.provideCodeLenses!({} as never, {} as never, next);

    assert.deepStrictEqual(result, []);
    assert.strictEqual(nextCalled, false);
  });

  test('resolveCodeLens returns undefined without calling next', () => {
    const middleware = createCodeLensSuppressionMiddleware();
    let nextCalled = false;
    const next = () => {
      nextCalled = true;
      return undefined;
    };

    const result = middleware.resolveCodeLens!({} as never, {} as never, next);

    assert.strictEqual(result, undefined);
    assert.strictEqual(nextCalled, false);
  });
});
