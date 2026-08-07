import * as assert from 'assert';
import { findFirstFailure, parseStepTrace, parseTrx } from '../../testRunner/trxParser';

function trxWithResults(resultsXml: string): string {
  return `<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    ${resultsXml}
  </Results>
</TestRun>`;
}

suite('trxParser', () => {
  suite('parseTrx', () => {
    test('parses a single passing result', () => {
      const trx = trxWithResults(`
        <UnitTestResult testId="1" testName="AddTwoNumbers" outcome="Passed">
          <Output><StdOut>Given a passing step
-&gt; done: Step() (0.0s)</StdOut></Output>
        </UnitTestResult>`);

      const results = parseTrx(trx);

      assert.strictEqual(results.length, 1);
      assert.strictEqual(results[0].testName, 'AddTwoNumbers');
      assert.strictEqual(results[0].outcome, 'Passed');
      assert.ok(results[0].stdOut.includes('-> done: Step() (0.0s)'));
      assert.strictEqual(results[0].errorMessage, undefined);
    });

    test('parses a failing result with ErrorInfo', () => {
      const trx = trxWithResults(`
        <UnitTestResult testId="1" testName="AddTwoNumbers" outcome="Failed">
          <Output>
            <StdOut>Given a step
-&gt; error: deliberate failure (0.0s)</StdOut>
            <ErrorInfo>
              <Message>deliberate failure</Message>
              <StackTrace>at Foo.feature:line 3</StackTrace>
            </ErrorInfo>
          </Output>
        </UnitTestResult>`);

      const results = parseTrx(trx);

      assert.strictEqual(results[0].outcome, 'Failed');
      assert.strictEqual(results[0].errorMessage, 'deliberate failure');
      assert.ok(results[0].stackTrace?.includes('Foo.feature:line 3'));
    });

    test('parses multiple UnitTestResult entries for a row-tests method', () => {
      const trx = trxWithResults(`
        <UnitTestResult testId="1" testName="AddNumbers (a: &quot;1&quot;)" outcome="Passed">
          <Output><StdOut>done</StdOut></Output>
        </UnitTestResult>
        <UnitTestResult testId="2" testName="AddNumbers (a: &quot;2&quot;)" outcome="Failed">
          <Output><StdOut>failed</StdOut></Output>
        </UnitTestResult>`);

      const results = parseTrx(trx);

      assert.strictEqual(results.length, 2);
      assert.strictEqual(results[0].outcome, 'Passed');
      assert.strictEqual(results[1].outcome, 'Failed');
    });

    test('unescapes XML entities in testName and stdOut', () => {
      const trx = trxWithResults(`
        <UnitTestResult testId="1" testName="A &amp; B &lt;test&gt;" outcome="Passed">
          <Output><StdOut>&quot;quoted&quot; &amp; &apos;single&apos;</StdOut></Output>
        </UnitTestResult>`);

      const results = parseTrx(trx);

      assert.strictEqual(results[0].testName, 'A & B <test>');
      assert.strictEqual(results[0].stdOut, `"quoted" & 'single'`);
    });

    test('handles a self-closing UnitTestResult with no Output', () => {
      const trx = trxWithResults(
        `<UnitTestResult testId="1" testName="Empty" outcome="NotExecuted" />`,
      );

      const results = parseTrx(trx);

      assert.strictEqual(results.length, 1);
      assert.strictEqual(results[0].stdOut, '');
      assert.strictEqual(results[0].errorMessage, undefined);
    });

    test('returns an empty array for a TRX with no results', () => {
      const results = parseTrx(trxWithResults(''));
      assert.deepStrictEqual(results, []);
    });
  });

  suite('parseStepTrace', () => {
    test('pairs each step line with its outcome arrow line', () => {
      const stdOut = [
        'Given a passing step',
        '-> done: StepDefinitions.GivenAPassingStep() (0.0s)',
        'When a failing step is executed',
        '-> error: deliberate failure for stack trace inspection (0.0s)',
        'Then this line is never reached',
        '-> skipped because of previous errors',
      ].join('\n');

      const entries = parseStepTrace(stdOut);

      assert.strictEqual(entries.length, 3);
      assert.strictEqual(entries[0].outcome, 'done');
      assert.strictEqual(entries[0].stepText, 'Given a passing step');
      assert.strictEqual(entries[1].outcome, 'error');
      assert.strictEqual(entries[1].detail, 'deliberate failure for stack trace inspection (0.0s)');
      assert.strictEqual(entries[2].outcome, 'skippedBecauseOfPreviousErrors');
      assert.strictEqual(entries[2].stepText, 'Then this line is never reached');
    });

    test('handles all seven TestTracer outcome prefixes', () => {
      const stdOut = [
        'Given done step',
        '-> done: M() (0.0s)',
        'Given error step',
        '-> error: boom (0.0s)',
        'Given skipped step',
        '-> skipped: reason',
        'Given skipped-because step',
        '-> skipped because of previous errors',
        'Given pending step',
        '-> pending: M(): not implemented',
        'Given ambiguous step',
        '-> binding error: Ambiguous match',
        'Given unbound step',
        '-> undefined: Given("unbound step")',
      ].join('\n');

      const entries = parseStepTrace(stdOut);

      assert.deepStrictEqual(
        entries.map((e) => e.outcome),
        [
          'done',
          'error',
          'skipped',
          'skippedBecauseOfPreviousErrors',
          'pending',
          'bindingError',
          'undefined',
        ],
      );
    });

    test('returns an empty array for stdout with no trace lines', () => {
      assert.deepStrictEqual(parseStepTrace('some unrelated console output'), []);
    });
  });

  suite('findFirstFailure', () => {
    test('finds the first error/bindingError/undefined entry, skipping done/skipped', () => {
      const stdOut = [
        'Given ok',
        '-> done: M() (0.0s)',
        'When bad',
        '-> error: boom (0.0s)',
        'Then unreached',
        '-> skipped because of previous errors',
      ].join('\n');
      const entries = parseStepTrace(stdOut);

      const failure = findFirstFailure(entries);

      assert.strictEqual(failure?.stepText, 'When bad');
      assert.strictEqual(failure?.outcome, 'error');
    });

    test('returns undefined when every step passed', () => {
      const entries = parseStepTrace('Given ok\n-> done: M() (0.0s)');
      assert.strictEqual(findFirstFailure(entries), undefined);
    });
  });
});
