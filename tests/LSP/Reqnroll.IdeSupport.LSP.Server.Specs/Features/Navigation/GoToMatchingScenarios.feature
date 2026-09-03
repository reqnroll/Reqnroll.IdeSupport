Feature: Go to matching scenarios

reqnroll/goToMatchingScenarios answers, for a hook binding in a C# file, which scenarios that
hook actually runs for (design doc F24). It backs the hook-match CodeLens: the lens reports the
count, and clicking it sends this request to get the destinations.

The position is the hook method's own source location, round-tripped verbatim from the lens's
command arguments, and the handler matches it exactly rather than searching nearby — so the
scenarios below send the method's line and column, not the attribute's.

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
				[BeforeScenario]
				public void BeforeScenario() { }

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

		Scenario: Subtract
			Given the first number is 10
		"""
	Then the feature step "the first number is 50" is reported as bound

Scenario: An unscoped BeforeScenario hook matches every scenario in the feature
	When matching scenarios are requested at line 7 column 14 in "CalculatorHooks.cs"
	Then 2 matching scenarios are returned
	And the matching scenarios include "Add"
	And the matching scenarios include "Subtract"

Scenario: A position that is not a hook returns no scenarios
	When matching scenarios are requested at line 10 column 15 in "CalculatorHooks.cs"
	Then 0 matching scenarios are returned

Scenario: A request against a feature file is ignored
	When matching scenarios are requested at line 3 column 5 in "Calculator.feature"
	Then 0 matching scenarios are returned
