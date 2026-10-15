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

  /**
   * VS Code's language-configuration mapper (`_mapIndentationRules` in the workbench) treats
   * BOTH patterns as mandatory: if either `increaseIndentPattern` or `decreaseIndentPattern` is
   * absent (or fails to compile), it `return`s early and the ENTIRE `indentationRules` block is
   * discarded — the increase pattern never takes effect, so Enter never indents. The key must
   * therefore always be present. This is the regression guard for #1002: the first attempt at the
   * fix deleted `decreaseIndentPattern` outright, which silently disabled indentation entirely.
   */
  test('indentationRules declares both patterns VS Code requires', () => {
    assert.ok(
      typeof rules.increaseIndentPattern === 'string' && rules.increaseIndentPattern.length > 0,
      'increaseIndentPattern must be present (VS Code drops the whole block otherwise)',
    );
    assert.ok(
      typeof rules.decreaseIndentPattern === 'string' && rules.decreaseIndentPattern.length > 0,
      'decreaseIndentPattern must be present even if it never matches — VS Code drops the whole ' +
        'indentationRules block when this key is missing, which silently disables indentation',
    );
    assert.doesNotThrow(() => new RegExp(rules.increaseIndentPattern!));
    assert.doesNotThrow(() => new RegExp(rules.decreaseIndentPattern!));
  });

  test('decreaseIndentPattern does not match steps, tables, tags, comments or blank lines', () => {
    // A decrease pattern fires on the line itself and cancels the increase from the previous line,
    // so it would outdent `Scenario:` right after `Rule:`/`Feature:`. It must never be a catch-all;
    // `(?!)` (match nothing) is the intended value, keeping the block alive without ever outdenting.
    const decrease = new RegExp(rules.decreaseIndentPattern!);
    for (const line of [...shouldNotIncrease, '    Given a user', '      | a | b |']) {
      assert.ok(
        !decrease.test(line),
        `decreaseIndentPattern must not match ${JSON.stringify(line)}`,
      );
    }
  });
});
