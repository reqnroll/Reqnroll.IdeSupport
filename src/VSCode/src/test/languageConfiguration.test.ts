import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Tests for the indentation rules in language-configuration.json.
 *
 * VS Code tests each rule's regular expression against the text of a single line
 * (`new RegExp(pattern).test(line)`): a line matching `increaseIndentPattern` indents the lines
 * after it, and a line matching `decreaseIndentPattern` is outdented. These tests evaluate the
 * patterns the same way, against representative Gherkin lines.
 */

interface IndentationRules {
  increaseIndentPattern?: string;
  decreaseIndentPattern?: string;
}

interface LanguageConfiguration {
  indentationRules: IndentationRules;
}

let rules: IndentationRules;

suite('language-configuration.json indentationRules', () => {
  suiteSetup(() => {
    const configPath = path.resolve(__dirname, '..', '..', 'language-configuration.json');
    const config = JSON.parse(fs.readFileSync(configPath, 'utf-8')) as LanguageConfiguration;
    rules = config.indentationRules;
  });

  const shouldIncrease = [
    'Feature: Login',
    'Feature:',
    '  Rule: Accounts',
    '  Rule:',
    '  Background: Given a user',
    '  Background:',
    '  Scenario: Login',
    '  Scenario:',
    '\tScenario: Login',
    '  Scenario Outline: Login as <user>',
    '  Scenario Template: Login as <user>',
    '  Example: Login',
    '    Examples: valid users',
    '    Examples:',
    '    Scenarios: valid users',
    '    Scenarios:',
    '  Scenario : spaced colon',
  ];

  const shouldNotIncrease = [
    '    Given a user',
    '    When Scenario: appears mid-step',
    '    And the "Feature:" keyword is quoted',
    '      | user  | password |',
    '      | alice | secret   |',
    '  @smoke @login',
    '  # Scenario: commented out',
    '    """',
    '',
    '   ',
    '  Scenarioish: not a keyword',
  ];

  for (const line of shouldIncrease) {
    test(`increaseIndentPattern matches ${JSON.stringify(line)}`, () => {
      assert.ok(new RegExp(rules.increaseIndentPattern!).test(line));
    });
  }

  for (const line of shouldNotIncrease) {
    test(`increaseIndentPattern does not match ${JSON.stringify(line)}`, () => {
      assert.ok(!new RegExp(rules.increaseIndentPattern!).test(line));
    });
  }

  test('decreaseIndentPattern does not match steps, tables, tags, comments or blank lines', () => {
    // A decrease pattern fires on the line itself and cancels the increase from the previous line,
    // so it would outdent `Scenario:` right after `Rule:`/`Feature:`. It must never be a catch-all.
    if (rules.decreaseIndentPattern === undefined) {
      return;
    }
    const decrease = new RegExp(rules.decreaseIndentPattern);
    for (const line of [...shouldNotIncrease, '    Given a user', '      | a | b |']) {
      assert.ok(
        !decrease.test(line),
        `decreaseIndentPattern must not match ${JSON.stringify(line)}`,
      );
    }
  });
});
