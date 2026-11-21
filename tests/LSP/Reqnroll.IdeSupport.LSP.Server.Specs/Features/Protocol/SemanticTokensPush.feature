Feature: Server-pushed semantic tokens for Visual Studio

Visual Studio's built-in LSP client cannot map Reqnroll's custom token types and does not reliably
pull semantic tokens, so for the Visual Studio client (--ide visualstudio) the server proactively
pushes encoded tokens via the custom reqnroll/semanticTokens notification. Every other client ignores
that notification and uses the standard pull-based flow, so the server must not push to them.

Scenario: The server pushes semantic tokens to the Visual Studio client
	Given the LSP server is started for IDE "visualstudio"
	When the feature file "push.feature" is opened with
		"""
		Feature: Pushed coloring

		Scenario: S
			When I press add
		"""
	Then the client receives a semantic tokens push for "push.feature"

Scenario: The server does not push semantic tokens to non-Visual-Studio clients
	Given the LSP server is started for IDE "vscode"
	When the feature file "nopush.feature" is opened with
		"""
		Feature: Pulled coloring

		Scenario: S
			When I press add
		"""
	Then the client receives no semantic tokens push

Scenario: After an edit the server pushes only the tokens on the line that changed
	Given the LSP server is started for IDE "visualstudio"
	When the feature file "delta.feature" is opened with
		"""
		Feature: Delta coloring

		@smoke
		Scenario: S
			When I press add
		"""
	And the feature file "delta.feature" is changed to
		"""
		Feature: Delta coloring

		@smoke @regression
		Scenario: S
			When I press add
		"""
	Then the first semantic tokens push for "delta.feature" is the complete token set
	And the second semantic tokens push for "delta.feature" covers only line 3

Scenario: An edit on a line without tokens is not pushed again
	Given the LSP server is started for IDE "visualstudio"
	When the feature file "same.feature" is opened with
		"""
		Feature: Same coloring

		Scenario: S
			When I press add
		"""
	And the feature file "same.feature" is changed to
		"""
		Feature: Same coloring

		Scenario: S
			When I press add
		"""
	Then the first semantic tokens push for "same.feature" is the complete token set
	And no further semantic tokens push for "same.feature" arrives

Scenario: An edited line is pushed again even when its tokens are unchanged
	Given the LSP server is started for IDE "visualstudio"
	When the feature file "edited.feature" is opened with
		"""
		Feature: Edited coloring

		Scenario: S
			When I press add
		"""
	And the feature file "edited.feature" is changed to
		"""
		Feature: Edited coloring

		Scenario: S
			When I press add twice
		"""
	Then the first semantic tokens push for "edited.feature" is the complete token set
	And the second semantic tokens push for "edited.feature" covers only line 4
