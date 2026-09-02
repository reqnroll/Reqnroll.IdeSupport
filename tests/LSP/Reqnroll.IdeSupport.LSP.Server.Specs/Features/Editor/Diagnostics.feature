Feature: Feature file diagnostics

The server pushes textDocument/publishDiagnostics for a feature file, combining Gherkin
parse errors (source "reqnroll.parser", design doc F4) with undefined and ambiguous step
diagnostics from the binding match set (source "reqnroll.binding", design doc F3) into the
single complete set LSP requires per URI.

Binding diagnostics are suppressed while the binding registry is not yet ready, so each
scenario that asserts on them opens a C# step definition file first — that is what makes the
registry valid on the Roslyn live path, without a build.

Background:
	Given the LSP server is started

# ── Undefined steps ──────────────────────────────────────────────────────────────

Scenario: An undefined step is published as a binding warning
	When the project is announced with output assembly "Sample.dll" for "Undefined.feature"
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
	And the feature file "Undefined.feature" is opened with
		"""
		Feature: Undefined

		Scenario: S
			When I press something nobody defined
		"""
	Then a "Warning" diagnostic from "reqnroll.binding" is published for "Undefined.feature" saying "Step definition not found."

Scenario: The undefined-step warning is reported on the step's own line
	When the project is announced with output assembly "Sample.dll" for "Line.feature"
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
	And the feature file "Line.feature" is opened with
		"""
		Feature: Line

		Scenario: S
			When I press add
			When I press something nobody defined
		"""
	Then a "Warning" diagnostic from "reqnroll.binding" is published for "Line.feature" on line 5

Scenario: A feature file whose steps are all bound carries no binding diagnostics
	When the project is announced with output assembly "Sample.dll" for "Bound.feature"
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
	And the feature file "Bound.feature" is opened with
		"""
		Feature: Bound

		Scenario: S
			When I press add
		"""
	Then no "reqnroll.binding" diagnostic is published for "Bound.feature"

# ── Clearing ─────────────────────────────────────────────────────────────────────

Scenario: The warning is withdrawn once a binding for the step appears
	When the project is announced with output assembly "Sample.dll" for "Clearing.feature"
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
	And the feature file "Clearing.feature" is opened with
		"""
		Feature: Clearing

		Scenario: S
			When I press the new button
		"""
	Then a "Warning" diagnostic from "reqnroll.binding" is published for "Clearing.feature" saying "Step definition not found."
	When the C# step definition file "Steps.cs" is changed to
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class Steps
			{
				[When("I press add")]
				public void WhenIPressAdd() { }

				[When("I press the new button")]
				public void WhenIPressTheNewButton() { }
			}
		}
		"""
	Then the published diagnostics for "Clearing.feature" are empty

# ── Parse errors ─────────────────────────────────────────────────────────────────

Scenario: A Gherkin parse error is published as a parser error
	When the feature file "Broken.feature" is opened with
		"""
		Feature: Broken

		Scenario: S
			When I press add
			| unattached | table |
		Nonsense line that is not Gherkin
		"""
	Then a "Error" diagnostic from "reqnroll.parser" is published for "Broken.feature"
