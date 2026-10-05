Feature: Keyword Completion — Visual Studio workarounds (F7)

Behaviours that exist specifically to work around Visual Studio 2022's LSP client quirks.
VS treats an empty CompletionList for a trigger-character request as "reject and revert
the typed character", so these scenarios must start the server with --ide visualstudio.

Background:
    Given the LSP server is started for IDE "visualstudio"

# ── Table row: cell separator prevents VS reverting the '|' character ─────────

Scenario: VS completion inside a table row returns cell separator instead of empty list
    When the feature file "TableRow.feature" is opened with
        """
        Feature: Calculator
        Scenario Outline: add
            Given the number is <n>
            Examples:
                | n |
                |4
        """
    And completions are requested at line 5 column 10 in "TableRow.feature"
    Then completions are returned
    And the completions include a keyword label "| "

Scenario: VS completion inside a table row does not include keyword completions
    When the feature file "TableRow.feature" is opened with
        """
        Feature: Calculator
        Scenario Outline: add
            Given the number is <n>
            Examples:
                | n |
                |4
        """
    And completions are requested at line 5 column 10 in "TableRow.feature"
    Then the completions do not include a label "@ignore"
