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

# ── Replacement range never extends past the caret (issue #561) ─────────────

Scenario: Keyword completion range does not extend past the caret when text follows it on the line
    When the feature file "German.feature" is opened with
        """
        # language: de
        Szenario:[scenario name]
        """
    And completions are requested at line 1 column 1 in "German.feature"
    Then completions are returned
    And every completion's textEdit range does not extend past column 1
