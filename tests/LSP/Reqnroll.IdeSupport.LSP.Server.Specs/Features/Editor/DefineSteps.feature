Feature: Define Steps (F6)

The server offers a code action to generate a C# step-definition stub when the
editor is positioned on an undefined step. The action returns a WorkspaceEdit that
creates the new .cs file and inserts the skeleton, then supplies a vscode.open
command so editors can open the new file immediately after applying the edit.

Background:
    Given the LSP server is started

# ── Code action availability ──────────────────────────────────────────────────

Scenario: A code action is offered when the cursor is on an undefined step
    When the project is announced with output assembly "Sample.dll" for "Undefined.feature"
    And the C# step definition file "EmptySteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class EmptySteps { } }
        """
    And the feature file "Undefined.feature" is opened with
        """
        Feature: Undefined
        Scenario: S
            When this step has no binding
        """
    And code actions are requested for "Undefined.feature" at line 2
    Then a code action titled "Define missing step" is available

# ── Workspace edit shape ──────────────────────────────────────────────────────

Scenario: The code action workspace edit creates and populates a step definition file
    When the project is announced with output assembly "Sample.dll" for "Scaffold.feature"
    And the C# step definition file "EmptySteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class EmptySteps { } }
        """
    And the feature file "Scaffold.feature" is opened with
        """
        Feature: Scaffold
        Scenario: S
            When this step needs scaffolding
        """
    And code actions are requested for "Scaffold.feature" at line 2
    Then a code action titled "Define missing step" is available
    And the code action edit creates a new C# file
    And the code action edit inserts step definition C# code

# ── Open command ──────────────────────────────────────────────────────────────

Scenario: The code action includes a vscode.open command to show the new file
    When the project is announced with output assembly "Sample.dll" for "Open.feature"
    And the C# step definition file "EmptySteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class EmptySteps { } }
        """
    And the feature file "Open.feature" is opened with
        """
        Feature: Open
        Scenario: S
            When this step should open the editor
        """
    And code actions are requested for "Open.feature" at line 2
    Then a code action titled "Define missing step" is available
    And the code action has a "vscode.open" command to open the new file

# ── Multiple undefined steps ─────────────────────────────────────────────────

Scenario: A bulk action is offered when there are multiple undefined steps
    When the project is announced with output assembly "Sample.dll" for "Multi.feature"
    And the C# step definition file "EmptySteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class EmptySteps { } }
        """
    And the feature file "Multi.feature" is opened with
        """
        Feature: Multi
        Scenario: S
            When first step has no binding
            And second step has no binding
        """
    And code actions are requested for "Multi.feature" at line 2
    Then a code action titled "Define all missing steps in file" is available

# ── No-op when nothing to define ────────────────────────────────────────────────

Scenario: No code action is offered when all steps are already bound
    When the project is announced with output assembly "Sample.dll" for "AllBound.feature"
    And the C# step definition file "BoundSteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class BoundSteps {
            [When("this step is bound")]
            public void WhenThisStepIsBound() { }
        } }
        """
    And the feature file "AllBound.feature" is opened with
        """
        Feature: AllBound
        Scenario: S
            When this step is bound
        """
    And code actions are requested for "AllBound.feature" at line 2
    Then no code action titled "Define missing step" is available
    And no code action titled "Define all missing steps in file" is available
