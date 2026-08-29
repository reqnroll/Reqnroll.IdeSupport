Feature: Go to Hooks

Sending reqnroll/goToHooks from a cursor in a .feature file returns the hook bindings
applicable at that position, filtered by context level (design doc F17).

# Shared setup: announce the project, open a binding file that contains three hook types
# plus one step definition (used as the synchronisation signal for registry readiness),
# then open the feature file and wait for its step to be reported as bound.
Background:
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
    And the C# step definition file "CalculatorHooks.cs" is opened with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class CalculatorHooks
            {
                [BeforeFeature]
                public static void BeforeFeature() { }

                [BeforeScenario]
                public void BeforeScenario() { }

                [BeforeStep]
                public void BeforeStep() { }

                [Given("the first number is (.*)")]
                public void GivenTheFirstNumberIs(int number) { }
            }
        }
        """
    And the feature file "Calculator.feature" is opened with
        """
        Feature: Calculator
        Scenario: Add
            Given the first number is 50
        """
    Then the feature step "the first number is 50" is reported as bound

# Feature text (0-based line numbers):
#   Line 0 — "Feature: Calculator"         → Feature-level context
#   Line 1 — "Scenario: Add"               → Scenario-level context
#   Line 2 — "    Given the first number…"  → Step-level context

Scenario: Feature-level cursor returns only feature-scoped hooks
    When go to hooks is requested at line 0 column 0 in "Calculator.feature"
    Then 1 hook result is returned
    And the hook results include a "BeforeFeature" hook
    And the hook results include a location in "CalculatorHooks.cs"

Scenario: Scenario-level cursor returns feature- and scenario-scoped hooks
    When go to hooks is requested at line 1 column 0 in "Calculator.feature"
    Then 2 hook results are returned
    And the hook results include a "BeforeFeature" hook
    And the hook results include a "BeforeScenario" hook

Scenario: Step-level cursor returns all hook types
    When go to hooks is requested at line 2 column 4 in "Calculator.feature"
    Then 3 hook results are returned
    And the hook results include a "BeforeFeature" hook
    And the hook results include a "BeforeScenario" hook
    And the hook results include a "BeforeStep" hook

Scenario: Request on a non-feature file returns no hooks
    When go to hooks is requested at line 0 column 0 in "CalculatorHooks.cs"
    Then 0 hook results are returned
