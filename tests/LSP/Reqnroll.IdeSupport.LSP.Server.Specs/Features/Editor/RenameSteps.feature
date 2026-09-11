Feature: Rename Steps (F16)

textDocument/rename from a cursor in a .cs step-definition file returns a WorkspaceEdit
that updates the attribute string literal and every matching step in feature files
(design doc F16).

The custom reqnroll/renameTargets request returns the list of renameable binding
expressions at the cursor; reqnroll/selectRenameTarget pre-selects one for the
subsequent rename when a method has multiple attributes.

# ── Simple rename from the C# side ────────────────────────────────────────────

Scenario: Renaming from the .cs file updates the attribute and all feature usages
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
    # Use "opened and saved to disk with" so FindAttributeLiteralAsync can read the file from disk.
    And the C# step definition file "Steps.cs" is opened and saved to disk with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [When("I press add")]
                public void WhenIPressAdd() { }
            }
        }
        """
    And the feature file "Calculator.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    # Line 7 (0-based) is the method declaration "public void WhenIPressAdd() { }"
    When rename is requested at line 7 column 20 in "Steps.cs" with new name "I choose add"
    Then a workspace edit is returned
    And the workspace edit contains a change in "Steps.cs"
    And the workspace edit contains a change in "Calculator.feature"
    And the workspace edit changes to "Steps.cs" include new text "I choose add"
    And the workspace edit changes to "Calculator.feature" include new text "I choose add"

# ── Parameterized step rename ──────────────────────────────────────────────────

Scenario: Renaming a parameterized step preserves the parameter slots in feature usages
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Param.feature"
    And the C# step definition file "Steps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [Given("the first number is (.*)")]
                public void GivenTheFirstNumberIs(int n) { }
            }
        }
        """
    And the feature file "Param.feature" is opened with
        """
        Feature: Param
        Scenario: S
            Given the first number is 42
        """
    Then the feature step "the first number is 42" is reported as bound
    # Line 7 (0-based) is the method declaration "public void GivenTheFirstNumberIs(int n) { }"
    When rename is requested at line 7 column 20 in "Steps.cs" with new name "the operand is (.*)"
    Then a workspace edit is returned
    And the workspace edit contains a change in "Param.feature"
    And the workspace edit changes to "Param.feature" include new text "the operand is 42"

# ── prepareRename: no binding at cursor → no range returned ───────────────────

Scenario: Rename is not available when the cursor is not on a step binding
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "NoRename.feature"
    And the C# step definition file "Steps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [When("I press add")]
                public void WhenIPressAdd() { }
            }
        }
        """
    And the feature file "NoRename.feature" is opened with
        """
        Feature: NoRename
        Scenario: S
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    # Use prepareRename (not rename) to check availability without triggering an error response.
    # Line 0 (0-based) = "using Reqnroll;" — no step binding → prepareRename returns null.
    When prepare rename is requested at line 0 column 0 in "Steps.cs"
    Then no prepare rename range is returned

# ── Rename from the .feature side ─────────────────────────────────────────────

Scenario: Renaming from a .feature file updates the attribute and all feature usages
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "FeatureRename.feature"
    # Use "opened and saved to disk with" so FindAttributeLiteralAsync can read the file from disk.
    And the C# step definition file "Steps.cs" is opened and saved to disk with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [When("I press add")]
                public void WhenIPressAdd() { }
            }
        }
        """
    And the feature file "FeatureRename.feature" is opened with
        """
        Feature: FeatureRename
        Scenario: Add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    # Line 2 (0-based) is "    When I press add" — cursor at col 9 is within the step text
    # Regression: prepareRename used to return the whole line (column 0-200), so VS Code seeded
    # the rename dialog with "    When I press add" (keyword and indentation included). Submitting
    # an edited copy of that back then duplicated the keyword when the resulting edit was applied
    # only at the step-text-only range HandleRenameAsync actually replaces, producing
    # "    When     When I choose add" in the feature file.
    When prepare rename is requested at line 2 column 9 in "FeatureRename.feature"
    Then the prepare rename range excludes the step keyword and indentation
    When rename is requested at line 2 column 9 in "FeatureRename.feature" with new name "I choose add"
    Then a workspace edit is returned
    And the workspace edit contains a change in "Steps.cs"
    And the workspace edit contains a change in "FeatureRename.feature"
    And the workspace edit changes to "Steps.cs" include new text "I choose add"
    And the workspace edit changes to "FeatureRename.feature" include new text "I choose add"

# ── Rename from the .feature side, parameterized step ─────────────────────────
# Regression: VS Code seeds the .feature rename dialog with the step's concrete text (real
# parameter values, since prepareRename's range covers the whole line) — not the binding's
# abstract expression. The submitted "new name" is therefore concrete text too (e.g.
# "I have 5 pickles", not "I have {int} cukes"). The rename must reconcile that concrete
# edit back to an abstract expression before validating/propagating it, otherwise the
# parameter-count check always fails and the rename silently no-ops.

Scenario: Renaming a parameterized step from the .feature file preserves the parameter slot
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "ParamFeatureRename.feature"
    # Use "opened and saved to disk with" so FindAttributeLiteralAsync can read the file from disk.
    And the C# step definition file "Steps.cs" is opened and saved to disk with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [Given("I have {int} cukes")]
                public void GivenIHaveCukes(int count) { }
            }
        }
        """
    And the feature file "ParamFeatureRename.feature" is opened with
        """
        Feature: ParamFeatureRename
        Scenario: S
            Given I have 5 cukes
        """
    Then the feature step "I have 5 cukes" is reported as bound
    # Line 2 (0-based) is "    Given I have 5 cukes" — cursor at col 20 is within "cukes".
    # The dialog is seeded with the concrete line and only the static word "cukes" is edited.
    When rename is requested at line 2 column 20 in "ParamFeatureRename.feature" with new name "I have 5 pickles"
    Then a workspace edit is returned
    And the workspace edit contains a change in "Steps.cs"
    And the workspace edit contains a change in "ParamFeatureRename.feature"
    And the workspace edit changes to "Steps.cs" include new text "I have {int} pickles"
    And the workspace edit changes to "ParamFeatureRename.feature" include new text "I have 5 pickles"

# ── prepareRename for .feature: undefined step must block dialog ───────────────

Scenario: Rename is not available for an undefined step in a .feature file
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "UndefinedRename.feature"
    And the C# step definition file "EmptySteps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample { [Binding] public class EmptySteps { } }
        """
    And the feature file "UndefinedRename.feature" is opened with
        """
        Feature: UndefinedRename
        Scenario: S
            When this step has no binding
        """
    Then the feature step "this step has no binding" is reported as unbound
    # Line 2 (0-based) is "    When this step has no binding" — undefined step
    # prepareRename must return null so VS Code suppresses the rename dialog
    When prepare rename is requested at line 2 column 9 in "UndefinedRename.feature"
    Then no prepare rename range is returned

# ── Validation failure surfaces as a real LSP error, not a silent null (issue #650) ────

Scenario: Renaming to a new name with a different parameter count fails with a real error, not a silent null
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "ParamMismatch.feature"
    And the C# step definition file "Steps.cs" is opened and saved to disk with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [Given("the first number is (.*)")]
                public void GivenTheFirstNumberIs(int n) { }
            }
        }
        """
    And the feature file "ParamMismatch.feature" is opened with
        """
        Feature: ParamMismatch
        Scenario: S
            Given the first number is 42
        """
    Then the feature step "the first number is 42" is reported as bound
    # Line 7 (0-based) is the method declaration "public void GivenTheFirstNumberIs(int n) { }".
    # The new name drops the (.*) parameter entirely, tripping Rule 4 (parameter count mismatch).
    When rename is requested at line 7 column 20 in "Steps.cs" with new name "the operand is" and an error is expected
    Then the rename fails with error message "Parameter count mismatch"

# ── Change-annotation negotiation (issue #70) ──────────────────────────────────
# A client advertising LSP 3.16 documentChanges + changeAnnotationSupport (e.g. VS Code) gets
# a grouped, labelled rename preview instead of the legacy Changes map. See
# docs/Rename-ChangeAnnotations-Implementation-Plan.md.

Scenario: A client that supports change annotations receives an annotated DocumentChanges edit
    Given the LSP client supports rename change annotations
    When the project is announced with output assembly "Sample.dll" for "Annotated.feature"
    And the C# step definition file "Steps.cs" is opened and saved to disk with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [When("I press add")]
                public void WhenIPressAdd() { }
            }
        }
        """
    And the feature file "Annotated.feature" is opened with
        """
        Feature: Annotated
        Scenario: Add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    When rename is requested at line 7 column 20 in "Steps.cs" with new name "I choose add"
    Then the workspace edit uses annotated document changes
    And the annotated workspace edit changes to "Steps.cs" include new text "I choose add"
    And the annotated workspace edit changes to "Annotated.feature" include new text "I choose add"

# ── Multi-attribute method: rename targets picker ─────────────────────────────

Scenario: A method with multiple step attributes returns one rename target per attribute
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Multi.feature"
    And the C# step definition file "Steps.cs" is opened with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class Steps
            {
                [Given("I press add")]
                [When("I press add")]
                [When("I invoke add")]
                public void SharedMethod() { }
            }
        }
        """
    And the feature file "Multi.feature" is opened with
        """
        Feature: Multi
        Scenario: S
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    # Line 9 (0-based) = "public void SharedMethod() { }"
    When rename targets are requested at line 9 column 20 in "Steps.cs"
    Then 3 rename targets are returned
    And the rename targets include expression "I press add"
    And the rename targets include expression "I invoke add"
