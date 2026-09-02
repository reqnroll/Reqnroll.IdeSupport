Feature: Go to Step Definition

Sending textDocument/definition from a cursor on a step in a feature file returns the
location of the binding method that matches it (design doc F5). The handler reads the
match set for the document's primary owner, so an undefined step yields no location
rather than an error.

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
			}
		}
		"""
	And the feature file "Calculator.feature" is opened with
		"""
		Feature: Calculator

		Scenario: Add
			Given the first number is 50
			Given a step nobody defined
		"""
	Then the feature step "the first number is 50" is reported as bound

Scenario: A bound step resolves to its binding method
	When go to definition is requested at line 3 column 15 in "Calculator.feature"
	Then 1 definition location is returned
	And the definition locations include "CalculatorSteps.cs"

Scenario: An undefined step resolves to no location
	When go to definition is requested at line 4 column 15 in "Calculator.feature"
	Then 0 definition locations are returned

Scenario: A position that is not on a step resolves to no location
	When go to definition is requested at line 0 column 3 in "Calculator.feature"
	Then 0 definition locations are returned

Scenario: Go to definition on a C# file is ignored
	When go to definition is requested at line 6 column 5 in "CalculatorSteps.cs"
	Then 0 definition locations are returned
