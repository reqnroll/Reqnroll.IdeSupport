Feature: Refresh coalescing

Editing a feature file drives a server-initiated workspace/semanticTokens/refresh, telling the
client its cached tokens are stale. The server debounces that request so a burst of keystrokes
costs the client one re-encode rather than one per character, while still guaranteeing the
refresh that does arrive reflects the final content.

The two halves fail in opposite directions -- unbounded refreshes are the issue #491 performance
shape, a missing or too-early refresh is stale highlighting -- so both are asserted together.
Each handler's debouncer has its own unit test; what those cannot show is the composed behaviour
over the wire, which is what this covers.

Scenario: A burst of edits coalesces into a bounded number of refresh requests
	Given the LSP server is started
	When the feature file "Debounce.feature" is opened with
		"""
		Feature: Debounce

		Scenario: Edit 0
			When I press add
		"""
	And the feature file "Debounce.feature" is edited 12 times in rapid succession
	Then at least 1 semantic tokens refresh request is sent
	# Measured: 13 document changes (the open plus 12 edits) coalesce into exactly 1 refresh.
	# The bound is deliberately loose rather than 1, so a slower machine whose burst straddles
	# the debounce window does not flake; it still fails loudly on the regression that matters,
	# which is one refresh per keystroke.
	And at most 4 semantic tokens refresh requests are sent

Scenario: The document reflects the last edit of a burst, not an earlier one
	Given the LSP server is started
	When the feature file "Debounce.feature" is opened with
		"""
		Feature: Debounce

		Scenario: Edit 0
			When I press add
		"""
	And the feature file "Debounce.feature" is edited 12 times in rapid succession
	Then at least 1 semantic tokens refresh request is sent
	When the document outline is requested for "Debounce.feature"
	Then the children of "Debounce" contain a symbol named "Edit 12" with kind "Method"
