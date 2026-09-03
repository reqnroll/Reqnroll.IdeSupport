Feature: reqnroll/projectFiles membership index

The server accepts reqnroll/projectFiles baseline notifications from IDE glue and
uses them to build an authoritative path → project membership index.  Structural
semantic tokens (keywords, tags, comments) must continue to be produced regardless
of whether a baseline has been received, because they do not depend on binding
discovery.

Scenario: Server accepts a project files baseline without error
	Given the LSP server is started
	When the project is announced with output assembly "bin/Debug/Sample.dll" for "Sample.csproj"
	And the project files baseline is announced for "Sample.csproj" with
		| path             | role    |
		| proto.feature    | Feature |
		| StepDefs.cs      | Binding |
	And the feature file "proto.feature" is opened with
		"""
		# leading comment
		@smoke
		Feature: Membership

		Scenario: First
			When I do something
		"""
	Then the semantic tokens include a "reqnroll.comment" token for "# leading comment"
	And the semantic tokens include a "reqnroll.tag" token for "@smoke"

Scenario: Structural tokens are produced for a feature file after a baseline that includes it
	Given the LSP server is started
	When the feature file "baseline.feature" is opened with
		"""
		Feature: Baseline

		Scenario: Check
			Given a step
		"""
	And the project is announced with output assembly "bin/Debug/Sample.dll" for "Sample.csproj"
	And the project files baseline is announced for "Sample.csproj" with
		| path              | role    |
		| baseline.feature  | Feature |
	When the semantic tokens are requested again
	Then the semantic tokens include a "reqnroll.keyword" token for "Feature:"

Scenario: Two projects in their own folders each own their own files
	Given the LSP server is started
	When the project "Home.csproj" is announced in folder "Home"
	And the project "Linking.csproj" is announced in folder "Linking"
	And the project files baseline is announced for "Home.csproj" with
		| path                  | role    |
		| Home/Home.feature     | Feature |
		| Home/HomeSteps.cs     | Binding |
	And the project files baseline is announced for "Linking.csproj" with
		| path                    | role    |
		| Linking/Linking.feature | Feature |
		| Linking/LinkingSteps.cs | Binding |
	And the C# step definition file "Home/HomeSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Home
		{
			[Binding]
			public class HomeSteps
			{
				[When("I press home")]
				public void WhenIPressHome() { }
			}
		}
		"""
	And the C# step definition file "Linking/LinkingSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Linking
		{
			[Binding]
			public class LinkingSteps
			{
				[When("I press linking")]
				public void WhenIPressLinking() { }
			}
		}
		"""
	And the feature file "Linking/Linking.feature" is opened with
		"""
		Feature: Linking

		Scenario: S
			When I press linking
			When I press home
		"""
	Then the feature step "I press linking" is reported as bound
	And the feature step "I press home" is reported as unbound

Scenario: Server handles a project files baseline with no files
	Given the LSP server is started
	When the project is announced with output assembly "bin/Debug/Sample.dll" for "Sample.csproj"
	And the project files baseline is announced for "Sample.csproj" with
		| path | role |
	And the feature file "empty-baseline.feature" is opened with
		"""
		Feature: EmptyBaseline

		Scenario: S
			When something happens
		"""
	Then the semantic tokens include a "reqnroll.keyword" token for "Feature:"
