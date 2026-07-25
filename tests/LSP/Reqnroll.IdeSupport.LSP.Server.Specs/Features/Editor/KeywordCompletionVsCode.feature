Feature: Keyword Completion — VS Code protocol-level capability check (F7)

The table-row cell-separator placeholder in CompletionHandler is a Visual Studio-only
workaround (VS 2022 treats an empty CompletionList for a trigger-character request as "reject
and revert the typed character"). These scenarios start the server with --ide vscode to prove
the workaround does not leak to VS Code, which handles an empty CompletionList correctly.

Background:
    Given the LSP server is started for IDE "vscode"

Scenario: VS Code completion inside a table row returns no items, not the VS placeholder
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
