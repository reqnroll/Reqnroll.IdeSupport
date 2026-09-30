Feature: Keyword Completion (F7)

textDocument/completion on a blank line or a Gherkin keyword in a .feature file returns
Gherkin keyword candidates (design doc F7).

Background:
    Given the LSP server is started

# ── Blank file: default keyword set ─────────────────────────────────────────

Scenario: Completion on a blank feature file returns common keywords
    When the feature file "Blank.feature" is opened with
        """

        """
    And completions are requested at line 0 column 0 in "Blank.feature"
    Then completions are returned
    And the completions include a keyword label "Feature: "

# ── FeatureLine ──────────────────────────────────────────────────────────────

Scenario: Completion at FeatureLine returns Feature keyword
    When the feature file "Partial.feature" is opened with
        """
        Feature
        """
    And completions are requested at line 0 column 7 in "Partial.feature"
    Then completions are returned
    And the completions include a keyword label "Feature: "

# ── ScenarioLine ─────────────────────────────────────────────────────────────

Scenario: Completion at ScenarioLine returns Scenario keywords
    When the feature file "WithFeature.feature" is opened with
        """
        Feature: Calculator
        Scen
        """
    And completions are requested at line 1 column 4 in "WithFeature.feature"
    Then completions are returned
    And the completions include a keyword label "Scenario: "

Scenario: Completion at ScenarioLine also returns Scenario Outline keyword
    When the feature file "WithFeature.feature" is opened with
        """
        Feature: Calculator
        Scen
        """
    And completions are requested at line 1 column 4 in "WithFeature.feature"
    Then completions are returned
    And the completions include a keyword label "Scenario Outline: "

# ── StepLine ─────────────────────────────────────────────────────────────────

Scenario: Completion at StepLine returns Given / When / Then keywords
    When the feature file "WithScenario.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add
            Gi
        """
    And completions are requested at line 2 column 6 in "WithScenario.feature"
    Then completions are returned
    And the completions include a keyword label "Given "
    And the completions include a keyword label "When "
    And the completions include a keyword label "Then "

# ── Table row: keyword completions suppressed ────────────────────────────────

Scenario: Completion inside a table row returns no items for non-VS clients
    When the feature file "TableRow.feature" is opened with
        """
        Feature: Calculator
        Scenario Outline: add
            Given the number is <n>
            Examples:
                | n |
                |4
        """
    And completions are requested at line 5 column 2 in "TableRow.feature"
    Then no completions are returned

Scenario: Completion inside a table row does not include keyword completions
    When the feature file "TableRow.feature" is opened with
        """
        Feature: Calculator
        Scenario Outline: add
            Given the number is <n>
            Examples:
                | n |
                |4
        """
    And completions are requested at line 5 column 2 in "TableRow.feature"
    Then the completions do not include a label "@tag1 "

# ── Language dialect: file-level language directive ─────────────────────────

Scenario: Completion returns keywords for the dialect specified in the feature file
    When the feature file "German.feature" is opened with
        """
        # language: de
        Funk
        """
    And completions are requested at line 1 column 4 in "German.feature"
    Then completions are returned
    And the completions include a keyword label "Funktionalität: "

# ── Tag keyword ────────────────────────────────────────────────────────────────

Scenario: Completion on a blank feature file also returns the tag keyword
    When the feature file "TagBlank.feature" is opened with
        """

        """
    And completions are requested at line 0 column 0 in "TagBlank.feature"
    Then completions are returned
    And the completions include a keyword label "@tag1 "

# ── Examples keyword ───────────────────────────────────────────────────────────

Scenario: Completion at ScenarioOutlineLine returns Examples keyword
    When the feature file "WithOutline.feature" is opened with
        """
        Feature: Calc
        Scenario Outline: Add <n>
            When I press <n>
        Exam
        """
    And completions are requested at line 3 column 4 in "WithOutline.feature"
    Then completions are returned
    And the completions include a keyword label "Examples: "

# ── Non-.feature file ─────────────────────────────────────────────────────────

Scenario: Completion request on a non-feature file returns no items
    Given the file "Notes.txt" is open with content
        """
        some text
        """
    When completions are requested at line 0 column 0 in "Notes.txt"
    Then no completions are returned

# ── No completion while editing an already-typed title (issue #818) ─────────

Scenario: Completion while editing the end of an already-typed scenario title returns no completions
    When the feature file "TitleEditEnd.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add numbersx
        """
    And completions are requested at line 1 column 22 in "TitleEditEnd.feature"
    Then no completions are returned

Scenario: Completion while editing the middle of an already-typed scenario title returns no completions
    When the feature file "TitleEditMiddle.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add two numbers
        """
    And completions are requested at line 1 column 13 in "TitleEditMiddle.feature"
    Then no completions are returned

Scenario: Completion while editing an already-typed Feature title returns no completions
    When the feature file "FeatureTitleEdit.feature" is opened with
        """
        Feature: Calculator Pro
        """
    And completions are requested at line 0 column 23 in "FeatureTitleEdit.feature"
    Then no completions are returned

# ── Replacement range never extends past the caret (issue #561) ─────────────

Scenario: Keyword completion range does not extend past the caret when text follows it on the line
    When the feature file "German.feature" is opened with
        """
        # language: de
        Funktionalität: F
        Szenario:[scenario name]
        """
    And completions are requested at line 2 column 1 in "German.feature"
    Then completions are returned
    And every completion's textEdit range does not extend past column 1

# ── Completions inside a table are suppressed entirely (issue #818 follow-up) ─

Scenario: Completion inside a fully-formed table cell returns no items
    When the feature file "TableCell.feature" is opened with
        """
        Feature: Calculator
        Scenario Outline: add
            Given the number is <n>
            Examples:
                | n |
                | 44 |
        """
    And completions are requested at line 5 column 6 in "TableCell.feature"
    Then no completions are returned

# ── "@" at the start of a line offers only tag completions (issue #818 follow-up) ─

Scenario: Typing @ at the start of a blank line offers only the tag completion
    When the feature file "TagAtStart.feature" is opened with
        """
        @
        """
    And completions are requested at line 0 column 1 in "TagAtStart.feature"
    Then completions are returned
    And every completion label starts with "@"
    And the completions include a keyword label "@tag1 "

# ── "@" on a line that already has another keyword offers nothing (issue #818 follow-up) ─

Scenario: Typing @ after an already-typed scenario title offers no completions
    When the feature file "TagAfterTitle.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add numbersx@
        """
    And completions are requested at line 1 column 23 in "TagAfterTitle.feature"
    Then no completions are returned

# ── A second "@" on the same line offers no completions (issue #818 follow-up) ─
#
# Gherkin does technically allow a second tag straight after a first one on the same line
# ("@tag1 @tag2"), so this is a deliberate simplification, not a strict grammar rule: per
# maintainer discussion on #818, a completion popup while composing tags is disruptive either
# way, so the same "still a prefix of some candidate" rule that excludes keywords after a tag
# also excludes a second tag after a first one, rather than special-casing it back in.

Scenario: Typing a second @ after an existing tag on the same line offers no completions
    When the feature file "SecondTag.feature" is opened with
        """
        @tag1 @
        """
    And completions are requested at line 0 column 7 in "SecondTag.feature"
    Then no completions are returned

# ── "@" where a tag is not grammatically allowed (issue #818 follow-up) ──────
#
# Directly on a step line (not a blank line before it), the caret resolves to step-definition
# completion, not keyword completion, so no tag can appear there at all regardless of the "@".

Scenario: Typing @ as part of an already-bound step's text offers no tag or keyword completions
    When the feature file "TagOnStep.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add
            Given a step@
        """
    And completions are requested at line 2 column 17 in "TagOnStep.feature"
    Then the completions do not include a label "@tag1 "
    And the completions do not include a label "Scenario: "

# ── Known limitation: a genuinely blank line between two existing steps ─────
#
# Strict Gherkin never allows a tag between two steps of the same scenario - only before the
# next Feature/Rule/Scenario/Examples block. The parser's own per-line expected-token state is
# coarser than that: on a truly blank line here it still includes TagLine (alongside
# ScenarioLine/ExamplesLine/RuleLine/the table separator), the same alternation that is
# genuinely valid on a scenario's very first, still-empty line. Typing "@" is at least narrowed
# to the one candidate that starts with it (no Scenario:/Examples:/Rule:/table-separator noise),
# but a tag suggestion still appears where strict Gherkin would not allow one. Fixing this
# precisely would need the completion context to reason from the already-parsed AST's block
# boundaries instead of the per-line parser state, which is out of scope here - tracked as a
# follow-up rather than silently left untested.

Scenario: Typing @ on a blank line strictly between two existing steps still offers a tag suggestion
    When the feature file "TagBetweenSteps.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add
            Given a step
        @
            When another step
        """
    And completions are requested at line 3 column 1 in "TagBetweenSteps.feature"
    Then completions are returned
    And every completion label starts with "@"
