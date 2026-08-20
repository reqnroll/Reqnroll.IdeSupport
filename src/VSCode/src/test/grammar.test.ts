import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as oniguruma from 'vscode-oniguruma';
import * as vsctm from 'vscode-textmate';

/**
 * Structural tests for the TextMate grammar (gherkin.tmLanguage.json).
 *
 * Validates the grammar JSON is well-formed and each repository entry
 * has the expected structure and regex patterns compile and match
 * the intended Gherkin constructs.
 */

interface GrammarNode {
  match: string;
  begin: string;
  end: string;
  while: string;
  name: string;
  patterns: GrammarNode[];
}

interface Grammar {
  patterns: { include: string }[];
  repository: Record<string, GrammarNode>;
}

let grammar: Grammar;
let tmGrammar: vsctm.IGrammar;

/**
 * Tokenizes a full document (line by line, carrying rule state across lines) using the real
 * TextMate engine, so tests can assert on the actual multi-line tokenizer behavior rather than
 * just the shape of individual regexes.
 */
function tokenizeLines(lines: string[]): vsctm.ITokenizeLineResult[] {
  let ruleStack = vsctm.INITIAL;
  const results: vsctm.ITokenizeLineResult[] = [];
  for (const line of lines) {
    const result = tmGrammar.tokenizeLine(line, ruleStack);
    results.push(result);
    ruleStack = result.ruleStack;
  }
  return results;
}

suite('gherkin.tmLanguage.json', () => {
  suiteSetup(async () => {
    const grammarPath = path.resolve(__dirname, '..', '..', 'syntaxes', 'gherkin.tmLanguage.json');
    const grammarSource = fs.readFileSync(grammarPath, 'utf-8');
    grammar = JSON.parse(grammarSource) as Grammar;

    const wasmPath = path.resolve(
      __dirname,
      '..',
      '..',
      'node_modules',
      'vscode-oniguruma',
      'release',
      'onig.wasm',
    );
    const wasmBin = fs.readFileSync(wasmPath).buffer;
    await oniguruma.loadWASM(wasmBin);

    const registry = new vsctm.Registry({
      onigLib: Promise.resolve({
        createOnigScanner: (patterns: string[]) => new oniguruma.OnigScanner(patterns),
        createOnigString: (s: string) => new oniguruma.OnigString(s),
      }),
      loadGrammar: (scopeName: string) =>
        Promise.resolve(
          scopeName === 'text.gherkin.feature'
            ? vsctm.parseRawGrammar(grammarSource, grammarPath)
            : null,
        ),
    });

    const loaded = await registry.loadGrammar('text.gherkin.feature');
    assert.ok(loaded, 'Failed to load gherkin.tmLanguage.json into the TextMate registry');
    tmGrammar = loaded!;
  });

  test('should have a top-level patterns array with include references', () => {
    assert.ok(Array.isArray(grammar.patterns));
    assert.ok(grammar.patterns.length >= 8);
    for (const ref of grammar.patterns) {
      assert.ok(typeof ref.include === 'string', `Expected include reference`);
    }
  });

  test('should have all required repository keys', () => {
    const expected = [
      'comments',
      'doc_strings',
      'tags',
      'feature_keywords',
      'step_keywords',
      'table_header_separator',
      'tables',
      'strings',
      'scenario_outline_placeholders',
      'numeric_literals',
    ];
    for (const key of expected) {
      assert.ok(key in grammar.repository, `Missing repository key: ${key}`);
    }
  });

  // ── Comments ────────────────────────────────────────────────────────────

  suite('comments', () => {
    const p = () => grammar.repository.comments.patterns[0];

    test('should have comment.line.gherkin scope', () => {
      assert.strictEqual(p().name, 'comment.line.gherkin');
    });

    test('should match # comments', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('# this is a comment'));
      assert.ok(re.test('  # indented comment'));
      assert.ok(!re.test('Given something'));
    });
  });

  // ── Tags ────────────────────────────────────────────────────────────────

  suite('tags', () => {
    const p = () => grammar.repository.tags.patterns[0];

    test('should match individual @tags anywhere on a line', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('@smoke'.match(re), ['@smoke']);
      const matches = '@smoke @regression @slow'.match(re);
      assert.strictEqual(matches?.length, 3);
    });

    // An @ immediately preceded by a word character is part of a larger token (an email address,
    // a handle embedded in a word) rather than a Gherkin tag, which always starts at a word
    // boundary. Without the (?<!\w) guard, "user@example.com" in step text highlighted "@example"
    // as a tag.
    test('does not match an @ embedded in an email address', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual("the user's email is user@example.com".match(re), null);
    });

    test('still matches a tag immediately after other punctuation', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('(@smoke)'.match(re), ['@smoke']);
    });
  });

  // ── Feature keywords ────────────────────────────────────────────────────

  suite('feature_keywords', () => {
    const p = () => grammar.repository.feature_keywords.patterns[0];

    test('should match each Gherkin block keyword', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('Feature: Login'));
      assert.ok(re.test('Rule: Access control'));
      assert.ok(re.test('Background:'));
      assert.ok(re.test('Scenario: Successful login'));
      assert.ok(re.test('Scenario Outline: Login variants'));
      assert.ok(re.test('Scenario Template: Login variants'));
      assert.ok(re.test('Examples:'));
      assert.ok(re.test('Example:'));
    });

    test('should match indented keywords', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('  Scenario: indented'));
    });

    test('should not match step keywords', () => {
      const re = new RegExp(p().match);
      assert.ok(!re.test('Given something'));
    });
  });

  // ── Step keywords ───────────────────────────────────────────────────────

  suite('step_keywords', () => {
    const p0 = () => grammar.repository.step_keywords.patterns[0];
    const p1 = () => grammar.repository.step_keywords.patterns[1];

    test('should match each Given/When/Then/And/But keyword', () => {
      const re = new RegExp(p0().match);
      assert.ok(re.test('Given I have 42'));
      assert.ok(re.test('When I press enter'));
      assert.ok(re.test('Then I see result'));
      assert.ok(re.test('And something else'));
      assert.ok(re.test('But not this'));
    });

    test('should match * step keyword as a separate pattern', () => {
      const re = new RegExp(p1().match);
      assert.ok(re.test('* some step'));
      assert.ok(re.test('  * indented asterisk step'));
    });

    test('should match indented step keywords', () => {
      const re = new RegExp(p0().match);
      assert.ok(re.test('    Given indented step'));
    });

    test('should have keyword.control.gherkin.step scope', () => {
      assert.strictEqual(p0().name, 'keyword.control.gherkin.step');
    });
  });

  // ── Doc strings ─────────────────────────────────────────────────────────

  suite('doc_strings', () => {
    const p = () => grammar.repository.doc_strings;

    test('should use begin/end for triple-quoted blocks anchored to the start of the line', () => {
      assert.strictEqual(p().begin, '^\\s*"""');
      assert.strictEqual(p().end, '^\\s*"""');
      assert.strictEqual(p().name, 'string.quoted.other.gherkin');
    });

    // Regression test for #463: syntax highlighting stayed stuck in the doc-string scope after
    // the closing """, because the doc_strings rule embedded the full text.html.markdown grammar
    // and one of its own multi-line constructs could swallow the closing delimiter's line before
    // the outer end pattern was ever re-tested.
    test('#463: highlighting must return to normal after the closing """', () => {
      const lines = [
        'Scenario: New bay with duplicated name',
        "  Given a bay called 'Bay1'",
        "  When I try to create a bay called 'Bay1'",
        '  Then I will see a create bay error:',
        '    """',
        '    This name is already used for another bay',
        '    """',
        '',
        'Scenario: New bay with no name',
        '  When I try to create a bay with no name',
        '  Then I will see a create bay error:',
      ];
      const results = tokenizeLines(lines);

      const closingDocStringLineIdx = 6; // '    """'
      const closingScopes = results[closingDocStringLineIdx].tokens.map((t) => t.scopes).flat();
      assert.ok(
        closingScopes.some((s) => s.includes('string.quoted.other.gherkin')),
        'The closing """ line itself should still be scoped as part of the doc string',
      );

      const nextScenarioLineIdx = 8; // 'Scenario: New bay with no name'
      const nextScenarioScopes = results[nextScenarioLineIdx].tokens.map((t) => t.scopes).flat();
      assert.ok(
        !nextScenarioScopes.some((s) => s.includes('string.quoted.other.gherkin')),
        `Expected highlighting to have exited the doc string after the closing """, but line ` +
          `${nextScenarioLineIdx} ("${lines[nextScenarioLineIdx]}") is still scoped as ` +
          `string.quoted.other.gherkin: ${JSON.stringify(nextScenarioScopes)}`,
      );
      assert.ok(
        nextScenarioScopes.some((s) => s.includes('keyword.control.gherkin')),
        `Expected the "Scenario:" keyword on line ${nextScenarioLineIdx} to be scoped as ` +
          `keyword.control.gherkin: ${JSON.stringify(nextScenarioScopes)}`,
      );

      const nextThenLineIdx = 10; // '  Then I will see a create bay error:'
      const nextThenScopes = results[nextThenLineIdx].tokens.map((t) => t.scopes).flat();
      assert.ok(
        nextThenScopes.some((s) => s.includes('keyword.control.gherkin.step')),
        `Expected the "Then" keyword on line ${nextThenLineIdx} to be scoped as ` +
          `keyword.control.gherkin.step: ${JSON.stringify(nextThenScopes)}`,
      );
    });

    // A """ appearing mid-line (not alone on its own line, ignoring leading indentation) is not
    // a valid doc-string delimiter per Gherkin's doc-string syntax, so it must not close the
    // block. Without the ^\s* anchor on begin/end, this content would end the doc string early.
    test('a """ that is not alone on its line does not close the doc string', () => {
      const lines = [
        '  Then I will see a create bay error:',
        '    """',
        '    The result was """quoted""" inline, not a real delimiter',
        '    """',
        'Scenario: New bay with no name',
      ];
      const results = tokenizeLines(lines);

      const midLineIdx = 2;
      const midLineTokens = results[midLineIdx].tokens;
      assert.ok(
        midLineTokens.every((t) => t.scopes.includes('string.quoted.other.gherkin')),
        `Expected every token on the mid-line to remain inside the doc string, but got: ` +
          `${JSON.stringify(midLineTokens)}`,
      );

      const nextScenarioIdx = 4;
      const nextScenarioScopes = results[nextScenarioIdx].tokens.map((t) => t.scopes).flat();
      assert.ok(
        nextScenarioScopes.some((s) => s.includes('keyword.control.gherkin')),
        'Expected the real closing """ (line 3, alone on its line) to have ended the doc ' +
          'string so the following Scenario: is highlighted normally',
      );
    });

    // Documents current, accepted behavior: an unterminated doc string has no closing delimiter
    // before EOF, so it (and everything after it) stays scoped as a string. This is standard
    // TextMate behavior for an unterminated begin/end block, not a bug — this test exists so a
    // future change to that behavior is a deliberate choice, not an accidental regression.
    test('an unterminated doc string stays open through the rest of the document', () => {
      const lines = ['    """', '    never closed', 'Scenario: unreachable as a real keyword'];
      const results = tokenizeLines(lines);

      const lastLineScopes = results[2].tokens.map((t) => t.scopes).flat();
      assert.ok(
        lastLineScopes.some((s) => s.includes('string.quoted.other.gherkin')),
        `Expected an unterminated doc string to still be open on the last line: ` +
          `${JSON.stringify(lastLineScopes)}`,
      );
    });
  });

  // ── Table header separator ──────────────────────────────────────────────

  suite('table_header_separator', () => {
    const p = () => grammar.repository.table_header_separator.patterns[0];

    test('should match separator rows with only dashes/colons/pipes', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('|------|--------|'));
      assert.ok(re.test('  |---|----|'));
      // Should NOT match data rows with text
      assert.ok(!re.test('| name | value |'));
    });
  });

  // ── Tables ──────────────────────────────────────────────────────────────

  suite('tables', () => {
    const p = () => grammar.repository.tables;

    test('should use begin/while for multi-row blocks', () => {
      assert.ok(typeof p().patterns[0].begin === 'string');
      assert.ok(typeof p().patterns[0].while === 'string');
    });

    test('should have begin pattern matching pipe start', () => {
      const re = new RegExp(p().patterns[0].begin);
      assert.ok(re.test('| name | value |'));
    });

    test('should have while pattern continuing on pipe lines', () => {
      const re = new RegExp(p().patterns[0].while);
      assert.ok(re.test('| value1 | value2 |'));
    });

    // Gherkin table cells escape a literal pipe as "\|" so it isn't mistaken for a column
    // separator. The naive [^|]+ cell pattern didn't know about that escape, so a cell like
    // "a message with a \| pipe" fractured into two cells at the escaped pipe.
    test('does not split a cell on an escaped \\| pipe', () => {
      const results = tokenizeLines(['| a message with a \\| pipe | second |']);
      const cellTokens = results[0].tokens.filter((t) =>
        t.scopes.includes('markup.table.cell.gherkin'),
      );
      assert.strictEqual(
        cellTokens.length,
        2,
        `Expected exactly 2 cells, got tokens: ` + JSON.stringify(results[0].tokens),
      );
    });
  });

  // ── Strings ─────────────────────────────────────────────────────────────

  suite('strings', () => {
    const p = () => grammar.repository.strings.patterns[0];

    test('should match double-quoted strings', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('"hello"'));
      assert.ok(re.test('"with space"'));
    });
  });

  // ── Scenario outline placeholders ───────────────────────────────────────

  suite('scenario_outline_placeholders', () => {
    const p = () => grammar.repository.scenario_outline_placeholders.patterns[0];

    test('should match <placeholder>', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('<name>'));
      assert.ok(re.test('<some-value>'));
    });

    test('should match a multi-word placeholder name', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('<bay name>'));
    });

    // [^>]+ was greedy across the whole line: "the count < 5 and total > 10" matched one
    // placeholder spanning "< 5 and total >". Real placeholder syntax never has whitespace
    // touching the brackets, so requiring \S immediately inside < and > distinguishes a
    // placeholder from < / > used as comparison operators (the common case, where they're
    // surrounded by spaces). Note: this does not catch the rarer spaceless form "a<5 and b>10".
    test('does not treat comparison operators as one placeholder', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('the count < 5 and total > 10'.match(re), null);
    });

    // Compound cases: a real placeholder sharing a line with comparison-operator "<"/">" usage.
    // These matter more than the isolated case above because a fix that merely happens to work
    // on a single condition could still misbehave once there's a real placeholder nearby for the
    // greedy backtracking to latch onto.

    test('finds a real placeholder while ignoring comparison operators before it', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('the <value> is < 5 and > 10'.match(re), ['<value>']);
    });

    test('finds a real placeholder while ignoring comparison operators earlier in the line', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('the value is < 5 and > 10 and aligned with <placeholder>'.match(re), [
        '<placeholder>',
      ]);
    });

    test('finds two real placeholders immediately adjacent to comparison operators', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('the value is < <highValue> and > <lowValue>'.match(re), [
        '<highValue>',
        '<lowValue>',
      ]);
    });

    // Documents that the known spaceless-form limitation (see the comment above) persists even
    // when a real placeholder is also present on the line — it isn't masked or fixed by the
    // presence of legitimate placeholder syntax elsewhere. If this ever starts failing because
    // the false match disappears, that's a welcome improvement; update the assertion rather than
    // treating it as a regression.
    test('known limitation: spaceless comparison operators still false-match alongside a real placeholder', () => {
      const re = new RegExp(p().match, 'g');
      assert.deepStrictEqual('the <value> is <5 and b>10'.match(re), ['<value>', '<5 and b>']);
    });
  });

  // ── Numeric literals ────────────────────────────────────────────────────

  suite('numeric_literals', () => {
    const p = () => grammar.repository.numeric_literals.patterns[0];

    test('should match integers and decimals', () => {
      const re = new RegExp(p().match);
      assert.ok(re.test('42'));
      assert.ok(re.test('3.14'));
    });

    test('should not match non-numeric words', () => {
      const re = new RegExp(p().match);
      assert.ok(!re.test('hello'));
    });
  });
});
