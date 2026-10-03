Feature: Define Steps quick fix trigger (issue #847)

Every "Define step(s)" code action carries the server command reqnroll.defineStepsTriggered.
A client runs it after applying the action's edit, which is how the server learns the quick fix
was picked. The command must be routed to its own handler without disturbing the other
workspace/executeCommand commands.

Background:
    Given the LSP server is started

Scenario: The Define Steps command is accepted by the server
    When the Define Steps trigger command is executed for "Steps.cs"
    Then the command completes without an error
    And no applyEdit request is sent

Scenario: Registering the Define Steps command does not break comment toggling
    When the feature file "Routing.feature" is opened with
        """
        Feature: Calculator
        """
    And the Define Steps trigger command is executed for "Steps.cs"
    And the toggle comment command is executed for "Routing.feature" on lines 0 to 0
    Then the edit replaces line 0 with "# Feature: Calculator"
