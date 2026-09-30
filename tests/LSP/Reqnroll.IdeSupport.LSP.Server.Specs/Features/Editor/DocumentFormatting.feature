Feature: Document Auto-formatting (F11)

Formats a .feature file via textDocument/formatting and textDocument/rangeFormatting,
fixing indentation, normalising tag whitespace, replacing repeated step keywords
with "And", and aligning data-table cells (design doc F11).

Background:
    Given the LSP server is started

# ── Full document formatting ─────────────────────────────────────────────────

Scenario: Misindented steps are fixed on format document
    When the feature file "Indented.feature" is opened with
        """
        Feature: Addition
        Scenario: Add
        Given I have 50
        When I add
        Then result is 50
        """
    And the document "Indented.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "    Given I have 50"
    And the formatted text contains "    When I add"
    And the formatted text contains "    Then result is 50"

Scenario: Repeated step keywords are replaced with And
    When the feature file "Keywords.feature" is opened with
        """
        Feature: Keywords
        Scenario: Steps
            Given first step
            Given second step
            When first action
            When second action
            Then first check
            Then second check
        """
    And the document "Keywords.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "    And second step"
    And the formatted text contains "    And second action"
    And the formatted text contains "    And second check"

Scenario: Data table cells are column-aligned
    When the feature file "Table.feature" is opened with
        """
        Feature: Table
        Scenario: Aligned
            Given a table
            | short | a very long header |
            | x | y |
        """
    And the document "Table.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "| short | a very long header |"
    And the formatted text contains "| x     | y                  |"

Scenario: Tag whitespace is normalised on format
    When the feature file "Tags.feature" is opened with
        """
          @tag1    @tag2
        Feature: Tagged
          @tag3
        Scenario: Sc
            Given step
        """
    And the document "Tags.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "@tag1 @tag2"

# ── Range formatting ─────────────────────────────────────────────────────────

Scenario: Range formatting returns edits for the specified range
    When the feature file "Range.feature" is opened with
        """
        Feature: Range
        Scenario: Range
        Given step one
        When step two
        Then step three
        """
    And range formatting is requested for "Range.feature" from line 2 to line 4
    Then formatting edits are returned

# ── Descriptions and comments are preserved ──────────────────────────────────

Scenario: Feature description text is not removed by formatting
    When the feature file "Desc.feature" is opened with
        """
        Feature: Description
          This is a feature description
          that spans multiple lines.

        Scenario: S
            Given a step
        """
    And the document "Desc.feature" is formatted
    Then the formatted text contains "This is a feature description"

Scenario: Comment lines are preserved verbatim by formatting
    When the feature file "Comments.feature" is opened with
        """
        # Top-level comment
        Feature: Comments
        Scenario: S
            # Step comment
            Given a step
        """
    And the document "Comments.feature" is formatted
    Then the formatted text contains "# Top-level comment"
    And the formatted text contains "# Step comment"

# ── Non-feature file ignored ─────────────────────────────────────────────────

Scenario: Non-feature file returns no formatting edits
    Given the file "readme.txt" is open with content
        """
        Some plain text
        """
    When the document "readme.txt" is formatted
    Then no formatting edits are returned

# ── On-type formatting (F12) ─────────────────────────────────────────────────

Scenario: On-type formatting aligns table columns when pipe is typed
    When the feature file "OnTypePipe.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | short | a very long header |
            | x | y |
        """
    And on-type formatting is requested for "OnTypePipe.feature" at line 4 column 7 with trigger "|"
    Then formatting edits are returned
    And the formatted text contains "| x     | y                  |"

Scenario: On-type formatting aligns table columns when newline is typed after table row
    When the feature file "OnTypeNewline.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | col1 | col2 |
            | short | longer value |

        """
    And on-type formatting is requested for "OnTypeNewline.feature" at line 5 column 0 with trigger "\n"
    Then formatting edits are returned
    And the formatted text contains "| col1  | col2         |"
    And the formatted text contains "| short | longer value |"

Scenario: On-type formatting returns no edits when cursor is not inside a table
    When the feature file "OnTypeStep.feature" is opened with
        """
        Feature: Step
        Scenario: NoTable
            Given a step without table
        """
    And on-type formatting is requested for "OnTypeStep.feature" at line 2 column 20 with trigger "|"
    Then no formatting edits are returned

Scenario: On-type formatting aligns table columns when tab is typed
    When the feature file "OnTypeTab.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | short | a very long header |
            | x | y |
        """
    And on-type formatting is requested for "OnTypeTab.feature" at line 4 column 7 with trigger "\t"
    Then formatting edits are returned
    And the formatted text contains "| x     | y                  |"

Scenario: On-type formatting adds trailing pipe for row missing it
    When the feature file "OnTypeTrailingPipe.feature" is opened with
        """
        Feature: Table
        Scenario: OnType
            Given a table
            | col1  | col2         |
            | short | longer value
        """
    And on-type formatting is requested for "OnTypeTrailingPipe.feature" at line 4 column 7 with trigger "|"
    Then formatting edits are returned
    And the formatted text contains "| short | longer value |"

# ── Empty Examples block (issue #827) ────────────────────────────────────────

Scenario: Format document does not crash when an Examples block has no table
    When the feature file "EmptyExamples.feature" is opened with
        """
        Feature: EmptyExamples
        Scenario Outline: Outline
            Given <x>
            Examples:
        """
    And the document "EmptyExamples.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "Examples:"

Scenario: Range formatting does not crash when an Examples block has no table
    When the feature file "EmptyExamplesRange.feature" is opened with
        """
        Feature: EmptyExamples
        Scenario Outline: Outline
            Given <x>
            Examples:
        """
    And range formatting is requested for "EmptyExamplesRange.feature" from line 2 to line 3
    Then formatting edits are returned

Scenario: On-type formatting does not crash when a tableless Examples block precedes a data table
    When the feature file "EmptyExamplesOnType.feature" is opened with
        """
        Feature: Mixed
        Scenario Outline: Outline
            Given <x>
            Examples:
        Scenario: WithTable
            Given a table
            | h |
        """
    And on-type formatting is requested for "EmptyExamplesOnType.feature" at line 6 column 2 with trigger "|"
    Then formatting edits are returned

Scenario: Format document preserves an Examples block with only a header row
    When the feature file "HeaderOnlyExamples.feature" is opened with
        """
        Feature: HeaderOnly
        Scenario Outline: Outline
            Given <x>
            Examples:
            | x |
        """
    And the document "HeaderOnlyExamples.feature" is formatted
    Then formatting edits are returned
    And the formatted text contains "| x |"
