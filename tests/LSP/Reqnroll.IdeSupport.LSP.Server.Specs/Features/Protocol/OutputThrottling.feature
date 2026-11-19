Feature: Output rate limit while editing

Visual Studio handles the server's publishDiagnostics notifications on its UI thread, so in a very
large feature file one publish per keystroke makes typing stutter. For clients that ask for it
(the ThrottlesOutputWhileEditing behaviour, set for Visual Studio) the server therefore publishes at
most once per interval while edits keep arriving, and always publishes the state after the last
edit. Other clients are not affected.

Each throttle has its own unit tests; what those cannot show is the composed behaviour over the
wire, which is what this covers.

Scenario: A burst of edits for Visual Studio collapses into a bounded number of diagnostics publishes
	Given the LSP server is started for IDE "visualstudio"
	When the feature file "Burst.feature" is opened with
		"""
		Feature: Throttle

		Scenario: Edit 0
			When I press add
		"""
	And the feature file "Burst.feature" is edited 12 times in rapid succession, the last edit introducing a parse error
	Then a "Error" diagnostic from "reqnroll.parser" is published for "Burst.feature"
	# Measured shape: the open and the first edit publish at once and the rest of the burst follows in one
	# further publish. The bound is deliberately loose, so a slower machine whose burst straddles an interval
	# does not flake; it still fails on the regression that matters, which is one publish per edit (13).
	And at most 7 diagnostics publishes are sent for "Burst.feature"

Scenario: A burst of edits for other clients still publishes diagnostics for every edit
	Given the LSP server is started for IDE "vscode"
	When the feature file "Burst.feature" is opened with
		"""
		Feature: Throttle

		Scenario: Edit 0
			When I press add
		"""
	And the feature file "Burst.feature" is edited 12 times in rapid succession, the last edit introducing a parse error
	Then a "Error" diagnostic from "reqnroll.parser" is published for "Burst.feature"
	And at least 10 diagnostics publishes are sent for "Burst.feature"
