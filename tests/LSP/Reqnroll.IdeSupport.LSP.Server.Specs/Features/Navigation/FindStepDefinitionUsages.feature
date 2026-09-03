Feature: Find Step Definition Usages

Sending textDocument/references from a cursor on a C# step binding method returns
Location entries pointing at every matching step in feature files — regardless of
whether those feature files are open in the editor (design doc F14).

Scenario: References for a bound step binding return the matching feature file location
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
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
	When references are requested at line 7 column 0 in "CalculatorSteps.cs"
	Then 1 reference is returned
	And the references include a location in "Calculator.feature"

Scenario: No references are returned for a step binding with no matching steps in open files
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "NoMatch.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
		        [Given("the first number is (.*)")]
		        public void GivenTheFirstNumberIs(int number) { }
		    }
		}
		"""
	And the feature file "NoMatch.feature" is opened with
		"""
		Feature: NoMatch
		Scenario: S
		    Given an unrelated step
		"""
	Then the feature step "an unrelated step" is reported as unbound
	When references are requested at line 7 column 0 in "CalculatorSteps.cs"
	Then 0 references are returned

# E2: caret on the [Given] attribute line should resolve (0-based line 6 = 1-based line 7 = attribute line)
Scenario: References resolve when the caret is on the binding attribute line
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
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
	When references are requested at line 6 column 0 in "CalculatorSteps.cs"
	Then 1 reference is returned
	And the references include a location in "Calculator.feature"

# P1: cursor not on any binding returns 0 results. The server internally distinguishes "not a
# binding" (HasBindingAtLocation=false) from "0 usages"; the VS client uses reqnroll/findStepUsages
# for the full three-state contract because OmniSharp's LocationOrLocationLinks converter does not
# support null serialization over textDocument/references in the spec test harness.
Scenario: Zero references are returned when the cursor is not on a binding
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
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
	When references are requested at line 0 column 0 in "CalculatorSteps.cs"
	Then 0 references are returned

# reqnroll/findStepUsages: the custom three-state request used by the VS client.
# Delivers JSON null for "not a binding" (textDocument/references cannot carry null).
# The "isBinding=true + empty locations" state (binding present, 0 usages) is covered
# by unit tests only: it requires connector-loaded bindings that the spec harness cannot
# provide with a non-existent Sample.dll output assembly.

Scenario: reqnroll/findStepUsages returns isBinding false when the cursor is not on a binding
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
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
	When step usages are requested at line 0 column 0 in "CalculatorSteps.cs"
	Then the step usages response has isBinding false

Scenario: reqnroll/findStepUsages returns isBinding true and matching locations for a bound step
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
		    [Binding]
		    public class CalculatorSteps
		    {
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
	When step usages are requested at line 6 column 0 in "CalculatorSteps.cs"
	Then the step usages response has isBinding true
	And 1 step usage is returned
	And the step usages include a location in "Calculator.feature"
	And the step usages include a non-empty step text

# ── Ownership: a binding file linked into two projects ──────────────────────────
#
# Home/SharedSteps.cs is physically under Home but is claimed by both projects' baselines.
# The design requires Find All Usages to union across every project that includes the binding:
# "a binding linked into N projects is used if a feature in any of the N references it."
# FindStepUsagesHandler and ReferencesHandler pass ResolveOwners as a project filter, so a
# regression that narrowed that filter to one owner would hide half the results.

Scenario: Usages of a binding linked into two projects union across both owners
	Given the LSP server is started
	When the project "Home.csproj" is announced in folder "Home"
	And the project "Linking.csproj" is announced in folder "Linking"
	And the project files baseline is announced for "Home.csproj" with
		| path                     | role    |
		| Home/SharedSteps.cs      | Binding |
		| Home/UsedInHome.feature  | Feature |
	And the project files baseline is announced for "Linking.csproj" with
		| path                           | role    |
		| Home/SharedSteps.cs            | Binding |
		| Linking/UsedInLinking.feature  | Feature |
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
	And the feature file "Home/UsedInHome.feature" is opened with
		"""
		Feature: UsedInHome
		Scenario: S
		    When I press shared
		"""
	And the feature file "Linking/UsedInLinking.feature" is opened with
		"""
		Feature: UsedInLinking
		Scenario: S
		    When I press shared
		"""
	Then the feature step "I press shared" is reported as bound
	When references are requested at line 7 column 0 in "Home/SharedSteps.cs"
	Then 2 references are returned
	And the references include a location in "Home/UsedInHome.feature"
	And the references include a location in "Linking/UsedInLinking.feature"
