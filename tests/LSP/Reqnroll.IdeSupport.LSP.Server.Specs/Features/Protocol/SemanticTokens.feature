Feature: Semantic token encoding over the wire

These scenarios drive the real textDocument/semanticTokens/full request and decode the 5-int
delta encoding back into named tokens, asserting the legend mapping and the non-overlap guarantee
across the LSP boundary.  No bindings are required: every asserted token is structural (keywords,
tags, comments, descriptions, doc strings, data tables, scenario-outline placeholders).

Scenario: Tags, comments and descriptions are tokenized
	Given the LSP server is started
	When the feature file "meta.feature" is opened with
		"""
		# a leading comment
		@smoke @fast
		Feature: Metadata

		This describes the feature.

		Scenario: S
			When I press add
		"""
	Then the semantic tokens include a "reqnroll.comment" token for "# a leading comment"
	And the semantic tokens include a "reqnroll.tag" token for "@smoke"
	And the semantic tokens include a "reqnroll.tag" token for "@fast"
	And the semantic tokens include a "reqnroll.description" token for "This describes the feature."

Scenario: Doc strings and data tables are tokenized
	Given the LSP server is started
	When the feature file "tables.feature" is opened with
		"""
		Feature: Data

		Scenario: S
			Given the operands entered
				| operand | type    |
				| 42      | integer |
			When I provide the formula
				```
				2 + 3
				```
		"""
	Then the semantic tokens include a "reqnroll.data_table_header" token for "operand"
	And the semantic tokens include a "reqnroll.data_table_header" token for "type"

Scenario: Scenario outline placeholders are tokenized as parameters and do not overlap
	Given the LSP server is started
	When the feature file "outline.feature" is opened with
		"""
		Feature: Outline

		Scenario Outline: Add two numbers
			Given there is a number <a> and <b>
			When I press <op>
		Examples:
			| a | b | op  |
			| 1 | 2 | add |
		"""
	Then the semantic tokens include a "reqnroll.scenario_outline_placeholder" token for "<a>"
	And the semantic tokens include a "reqnroll.scenario_outline_placeholder" token for "<b>"
	And the semantic tokens include a "reqnroll.scenario_outline_placeholder" token for "<op>"
	And the semantic tokens are non-overlapping

Scenario: Steps are not marked as errors when no bindings are available
	Given the LSP server is started
	When the feature file "plain.feature" is opened with
		"""
		Feature: Addition

		Scenario: Add
			Given I have entered 50 into the calculator
			When I press add
		"""
	Then the semantic tokens do not include any "reqnroll.undefined_step" token

Scenario: The server advertises and serves textDocument/semanticTokens/range (issue #123)
	Given the LSP server is started
	Then the server advertises range support for semantic tokens
	When the feature file "meta.feature" is opened with
		"""
		# a leading comment
		@smoke @fast
		Feature: Metadata

		This describes the feature.

		Scenario: S
			When I press add
		"""
	And the semantic tokens for the whole-document range are requested
	Then the semantic tokens include a "reqnroll.comment" token for "# a leading comment"
	And the semantic tokens include a "reqnroll.tag" token for "@smoke"

Scenario: Visual Studio is not offered pull support, only the legend it needs to decode pushed tokens
	Given the LSP server is started for IDE "visualstudio"
	Then the server does not advertise pull support for semantic tokens
	And the server advertises a semantic token legend
