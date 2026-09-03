Feature: Inlay hints for step bindings

textDocument/inlayHint annotates each matched step in a feature file with the binding method
it resolves to (design doc F23). The hint is anchored at the end of the step's own line and
labelled with the class and method, so the reader can see which binding runs without leaving
the feature file. Unmatched steps get no hint.

Background:
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

				[When("I add the numbers")]
				public void WhenIAddTheNumbers() { }
			}
		}
		"""
	And the feature file "Calculator.feature" is opened with
		"""
		Feature: Calculator

		Scenario: Add
			Given the first number is 50
			When I add the numbers
			Then nobody defined this step
		"""
	Then the feature step "the first number is 50" is reported as bound

Scenario: Each matched step is annotated with its binding method
	When inlay hints are requested for "Calculator.feature" from line 1 to line 7
	Then 2 inlay hints are returned
	And an inlay hint on line 4 has label "GivenTheFirstNumberIs"
	And an inlay hint on line 5 has label "WhenIAddTheNumbers"

Scenario: An unmatched step carries no inlay hint
	When inlay hints are requested for "Calculator.feature" from line 1 to line 7
	Then no inlay hint is anchored on line 6

Scenario: Hints outside the requested range are not returned
	When inlay hints are requested for "Calculator.feature" from line 1 to line 4
	Then 1 inlay hint is returned
	And an inlay hint on line 4 has label "GivenTheFirstNumberIs"
