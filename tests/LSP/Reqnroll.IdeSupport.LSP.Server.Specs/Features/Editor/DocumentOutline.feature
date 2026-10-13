Feature: Document Outline (F9)

textDocument/documentSymbol returns a nested hierarchy of Gherkin document elements
so the IDE can populate its Outline / Structure panel and allow click-to-navigate.

Background:
    Given the LSP server is started

# ── Feature-only file ────────────────────────────────────────────────────────

Scenario: Feature with no scenarios returns a Feature symbol
    When the feature file "Feature.feature" is opened with
        """
        Feature: Calculator
        """
    And the document outline is requested for "Feature.feature"
    Then the outline contains 1 top-level symbol
    And the first top-level symbol has name "Calculator" and kind "Module"

# Regression (#959): an untitled "Feature:" (transient while typing) must yield a non-empty
# symbol name; empty names make LSP clients reject the whole documentSymbol response.
Scenario: Untitled Feature falls back to the keyword as its symbol name
    When the feature file "UntitledFeature.feature" is opened with
        """
        Feature:
        """
    And the document outline is requested for "UntitledFeature.feature"
    Then the outline contains 1 top-level symbol
    And the first top-level symbol has name "Feature" and kind "Module"

# ── Scenario as child ────────────────────────────────────────────────────────

Scenario: Scenario appears as a child of Feature
    When the feature file "WithScenario.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add two numbers
            Given I have entered 50 into the calculator
            And I have entered 70 into the calculator
            When I press add
            Then the result should be 120 on the screen
        """
    And the document outline is requested for "WithScenario.feature"
    Then the first top-level symbol has name "Calculator" and kind "Module"
    And the first child of "Calculator" has name "Add two numbers" and kind "Method"

# ── Steps as children of scenario ────────────────────────────────────────────

Scenario: Steps appear as children of Scenario
    When the feature file "WithSteps.feature" is opened with
        """
        Feature: F
        Scenario: S
            Given a step
            When something happens
            Then all is well
        """
    And the document outline is requested for "WithSteps.feature"
    Then the children of "S" contain a symbol named "Given a step" with kind "Field"
    And the children of "S" contain a symbol named "When something happens" with kind "Field"
    And the children of "S" contain a symbol named "Then all is well" with kind "Field"

# ── Background ────────────────────────────────────────────────────────────────

Scenario: Background appears as a child of Feature with kind Constructor
    When the feature file "WithBackground.feature" is opened with
        """
        Feature: F
        Background:
            Given a setup step
        Scenario: S
            Given a step
        """
    And the document outline is requested for "WithBackground.feature"
    Then the first child of "F" has name "Background" and kind "Constructor"

# ── Rule ─────────────────────────────────────────────────────────────────────

Scenario: Rule appears as a child of Feature with kind Namespace
    When the feature file "WithRule.feature" is opened with
        """
        Feature: F
        Rule: Business rule
        Scenario: S
            Given a step
        """
    And the document outline is requested for "WithRule.feature"
    Then the first child of "F" has name "Business rule" and kind "Namespace"

Scenario: Scenario inside Rule is a child of the Rule symbol
    When the feature file "RuleWithScenario.feature" is opened with
        """
        Feature: F
        Rule: R
        Scenario: S inside rule
            Given a step
        """
    And the document outline is requested for "RuleWithScenario.feature"
    Then the first child of "R" has name "S inside rule" and kind "Method"

# Regression (#959): an untitled "Rule:" must fall back to the keyword as its symbol name.
Scenario: Untitled Rule falls back to the keyword as its symbol name
    When the feature file "UntitledRule.feature" is opened with
        """
        Feature: F
        Rule:
        Scenario: S
            Given a step
        """
    And the document outline is requested for "UntitledRule.feature"
    Then the first child of "F" has name "Rule" and kind "Namespace"

# ── Scenario Outline + Examples ───────────────────────────────────────────────

Scenario: Scenario Outline has kind Method and Examples as a child
    When the feature file "WithOutline.feature" is opened with
        """
        Feature: F
        Scenario Outline: SO
            Given the number is <n>
            Examples:
                | n |
                | 1 |
                | 2 |
        """
    And the document outline is requested for "WithOutline.feature"
    Then the first child of "F" has name "SO" and kind "Method"
    And the children of "SO" contain a symbol named "Examples" with kind "Array"

# ── Multiple scenarios ────────────────────────────────────────────────────────

Scenario: Multiple scenarios all appear as siblings under Feature
    When the feature file "Multi.feature" is opened with
        """
        Feature: F
        Scenario: S1
            Given a step
        Scenario: S2
            Given a step
        Scenario: S3
            Given a step
        """
    And the document outline is requested for "Multi.feature"
    Then "F" has 3 children

# ── Non-.feature file ─────────────────────────────────────────────────────────

Scenario: Document outline request on a non-feature file returns no symbols
    Given the file "Notes.txt" is open with content
        """
        some text
        """
    When the document outline is requested for "Notes.txt"
    Then the outline is empty
