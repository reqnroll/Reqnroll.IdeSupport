@discovery
Feature: Bindings from source when connector discovery fails

When the out-of-process connector cannot discover bindings from the compiled assembly, the
server reads them from the project's source files instead, so steps are not shown as undefined
until each step-definition file is opened. The user is told in the IDE's Reqnroll output pane
why, since that is where they look when steps unexpectedly show as undefined.

Scenario: A failed connector run still finds the bindings in closed step-definition files
	Given the file "Steps.cs" exists on disk with
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
	And the file "Calc.feature" exists on disk with
		"""
		Feature: Calc

		Scenario: S
			When I press add
			When I press a button nobody defines
		"""
	When the project is announced with an output assembly the connector cannot load
	And the feature file "Calc.feature" is opened with
		"""
		Feature: Calc

		Scenario: S
			When I press add
			When I press a button nobody defines
		"""
	Then a warning containing "Binding discovery from the compiled assembly failed" is sent to the Reqnroll output pane within 60 seconds
	And the feature step "I press add" is reported as bound within 30 seconds
	And the feature step "I press a button nobody defines" is reported as unbound
