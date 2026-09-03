Feature: Find Unused Step Definitions (F15)

reqnroll/findUnusedStepDefinitions scans all binding registries and returns
step-definition expressions that have no matching steps across all open feature files
(design doc F15).

Bindings discovered via Roslyn source analysis (open .cs files) are included;
connector-loaded bindings (build artifacts) are also supported when available.

# ── Single unused binding ───────────────────────────────────────────────────────

Scenario: An unused step definition is reported
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
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

                [When("I press multiply")]
                public void WhenIPressMultiply() { }
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
    When unused step definitions are requested
    Then 1 unused step definition is returned
    And the unused step definitions include expression "I press multiply"

# ── All bindings used ──────────────────────────────────────────────────────────

Scenario: A fully-used binding set reports zero unused definitions
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Full.feature"
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
    And the feature file "Full.feature" is opened with
        """
        Feature: Full
        Scenario: Add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    When unused step definitions are requested
    Then 0 unused step definitions are returned

# ── Unused across multiple feature files ───────────────────────────────────────

Scenario: A step unused in all feature files is reported
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "A.feature"
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

                [When("I press subtract")]
                public void WhenIPressSubtract() { }
            }
        }
        """
    And the feature file "A.feature" is opened with
        """
        Feature: A
        Scenario: Add
            When I press add
        """
    And the feature file "B.feature" is opened with
        """
        Feature: B
        Scenario: Other add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    When unused step definitions are requested
    Then 1 unused step definition is returned
    And the unused step definitions include expression "I press subtract"
    And the unused step definitions do not include expression "I press add"

# ── Multi-attribute method: only unused attributes listed ──────────────────────

Scenario: Only unused expressions are returned when a method has multiple attributes
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
                [When("I press multiply")]
                [Then("I press equals")]
                [When("I press add")]
                public void SharedMethod() { }
            }
        }
        """
    And the feature file "Multi.feature" is opened with
        """
        Feature: Multi
        Scenario: Add
            When I press add
        """
    Then the feature step "I press add" is reported as bound
    When unused step definitions are requested
    Then 2 unused step definitions are returned
    And the unused step definitions include expression "I press multiply"
    And the unused step definitions include expression "I press equals"
    And the unused step definitions do not include expression "I press add"

# ── Deleted C# source file is removed from the registry ──────────────────────

Scenario: Deleting a C# source file removes its step definitions from Find Unused results
    Given the LSP server is started
    When the project is announced with output assembly "Sample.dll" for "Delete.feature"
    And the C# step definition file "StepsToDelete.cs" is opened with
        """
        using Reqnroll;
        namespace Sample
        {
            [Binding]
            public class StepsToDelete
            {
                [When("I press delete")]
                public void WhenIPressDelete() { }
            }
        }
        """
    And the feature file "Delete.feature" is opened with
        """
        Feature: Delete
        Scenario: Delete
            When this step has no binding
        """
    When unused step definitions are requested
    Then 1 unused step definition is returned
    And the unused step definitions include expression "I press delete"
    When the C# step definition file "StepsToDelete.cs" is deleted
    And unused step definitions are requested
    Then 0 unused step definitions are returned

# ── Ownership: a binding file linked into two projects ──────────────────────────
#
# Find Unused is the mirror of Find Usages and must intersect where that unions: a binding is
# unused only if unused in *every* including project. The design calls a false "unused" here
# actively harmful, since it invites deletion of live code. The behaviour currently falls out of
# FindUnusedStepDefinitionsService querying the match cache with no project filter; these
# scenarios pin it so a later change that adds one has to stay correct.

Scenario: A binding used only through the second owning project is not reported unused
    Given the LSP server is started
    When the project "Home.csproj" is announced in folder "Home"
    And the project "Linking.csproj" is announced in folder "Linking"
    And the project files baseline is announced for "Home.csproj" with
        | path                    | role    |
        | Home/SharedSteps.cs     | Binding |
        | Home/Unrelated.feature  | Feature |
    And the project files baseline is announced for "Linking.csproj" with
        | path                          | role    |
        | Home/SharedSteps.cs           | Binding |
        | Linking/UsedInLinking.feature | Feature |
    And the C# step definition file "Home/SharedSteps.cs" is opened with
        """
        using Reqnroll;
        namespace Shared
        {
            [Binding]
            public class SharedSteps
            {
                [When("I press shared")]
                public void WhenIPressShared() { }
            }
        }
        """
    And the feature file "Home/Unrelated.feature" is opened with
        """
        Feature: Unrelated
        Scenario: S
            When I press something else entirely
        """
    And the feature file "Linking/UsedInLinking.feature" is opened with
        """
        Feature: UsedInLinking
        Scenario: S
            When I press shared
        """
    Then the feature step "I press shared" is reported as bound
    When unused step definitions are requested
    Then the unused step definitions do not include expression "I press shared"

# The source deliberately lives under Linking, the project announced *second*: both registries
# report the binding and the rows are deduplicated to one, so attributing by first-seen order
# would answer "Home" here. Only the folder-ownership rule gives "Linking".

Scenario: An unused linked binding is attributed to the project whose folder holds its source
    Given the LSP server is started
    When the project "Home.csproj" is announced in folder "Home"
    And the project "Linking.csproj" is announced in folder "Linking"
    And the project files baseline is announced for "Home.csproj" with
        | path                    | role    |
        | Linking/SharedSteps.cs     | Binding |
        | Home/Unrelated.feature  | Feature |
    And the project files baseline is announced for "Linking.csproj" with
        | path                          | role    |
        | Linking/SharedSteps.cs           | Binding |
        | Linking/AlsoUnrelated.feature | Feature |
    And the C# step definition file "Linking/SharedSteps.cs" is opened with
        """
        using Reqnroll;
        namespace Shared
        {
            [Binding]
            public class SharedSteps
            {
                [When("I press shared")]
                public void WhenIPressShared() { }
            }
        }
        """
    And the feature file "Home/Unrelated.feature" is opened with
        """
        Feature: Unrelated
        Scenario: S
            When I press something else entirely
        """
    And the feature file "Linking/AlsoUnrelated.feature" is opened with
        """
        Feature: AlsoUnrelated
        Scenario: S
            When I press yet another thing
        """
    Then the feature step "I press yet another thing" is reported as unbound
    When unused step definitions are requested
    Then the unused step definitions include expression "I press shared"
    And the unused step definition "I press shared" is attributed to project "Linking"
