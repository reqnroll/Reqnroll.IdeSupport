import * as assert from 'assert';
import { formatBindingAttribute, formatMethodName } from '../../util/stepDefinitionItems';

// Row format shared with the Visual Studio and Rider step-definition lists (issue #757).
suite('stepDefinitionItems', () => {
  const base = { sourceLine: 0, sourceChar: 0 };

  suite('formatBindingAttribute', () => {
    test('shows the attribute with its expression', () => {
      assert.strictEqual(
        formatBindingAttribute({
          ...base,
          stepDefinitionType: 'Given',
          bindingExpression: 'the sum is {int}',
        }),
        '[Given("the sum is {int}")]',
      );
    });

    test('shows the bare attribute for a method-name-style binding', () => {
      assert.strictEqual(formatBindingAttribute({ ...base, stepDefinitionType: 'When' }), '[When]');
    });

    test('shows the quoted expression alone when the keyword is unknown (older server)', () => {
      assert.strictEqual(
        formatBindingAttribute({ ...base, bindingExpression: 'a step' }),
        '"a step"',
      );
    });

    test('is undefined when neither keyword nor expression is known', () => {
      assert.strictEqual(formatBindingAttribute({ ...base }), undefined);
    });
  });

  suite('formatMethodName', () => {
    test('joins class and method, or uses whichever is known', () => {
      assert.strictEqual(
        formatMethodName({ ...base, className: 'Steps', methodName: 'AStep' }),
        'Steps.AStep',
      );
      assert.strictEqual(formatMethodName({ ...base, methodName: 'AStep' }), 'AStep');
      assert.strictEqual(formatMethodName({ ...base, className: 'Steps' }), 'Steps');
    });
  });
});
