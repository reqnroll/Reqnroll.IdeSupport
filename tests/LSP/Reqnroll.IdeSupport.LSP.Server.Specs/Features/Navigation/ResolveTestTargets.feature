Feature: Resolve test targets

reqnroll/resolveTestTargets maps a range in a feature file to the generated test method(s) it
produces, so a client's Run/Debug action knows what to hand the test runner (design doc F26).

The resolver deliberately reads the generated <feature>.feature.cs code-behind rather than
predicting Reqnroll's naming rules, so these scenarios put a realistic code-behind on disk
next to the feature file — in a real project it is a build output no editor has open, which is
why it is written to disk rather than opened over LSP. Row-test targets are counted from the
row attributes of the detected test framework, so the project announces the matching Reqnroll
package.

Background:
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature" referencing package "Reqnroll.xUnit"
	And the file "Calculator.feature.cs" exists on disk with
		"""
		using Xunit;
		namespace Sample
		{
			public partial class CalculatorFeature
			{
				[Fact]
				public void AddTwoNumbers() { }

				[Theory]
				[InlineData("1")]
				[InlineData("2")]
				public void AddManyNumbers(string first) { }
			}
		}
		"""
	And the feature file "Calculator.feature" is opened with
		"""
		Feature: Calculator

		Scenario: Add two numbers
			Given the first number is 50
			When I add the numbers

		Scenario Outline: Add many numbers
			Given the first number is <first>
			When I add the numbers

		Examples:
			| first |
			| 1     |
			| 2     |
		"""

Scenario: A range inside a plain scenario resolves to that scenario's test method
	When test targets are resolved for "Calculator.feature" from line 3 to line 4
	Then 1 test target is returned
	And a test target has method "AddTwoNumbers" on type "Sample.CalculatorFeature"

Scenario: A range inside a Scenario Outline resolves to one target per Examples row
	When test targets are resolved for "Calculator.feature" from line 7 to line 8
	Then 2 test targets are returned
	And the test targets are parameterized
	And a test target has method "AddManyNumbers"

Scenario: A range inside one Examples row resolves to just that row's target
	When test targets are resolved for "Calculator.feature" from line 14 to line 15
	Then 1 test target is returned
	And a test target has method "AddManyNumbers"

# A range outside any scenario resolves to the first scenario's target, so a Run action invoked
# from the Feature: line still has something to run rather than silently doing nothing.

Scenario: A range outside any scenario resolves to the first scenario's target
	When test targets are resolved for "Calculator.feature" from line 1 to line 2
	Then 1 test target is returned
	And a test target has method "AddTwoNumbers" on type "Sample.CalculatorFeature"
