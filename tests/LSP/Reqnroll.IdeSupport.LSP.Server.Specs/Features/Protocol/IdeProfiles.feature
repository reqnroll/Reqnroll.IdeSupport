Feature: Shared semantic token legend across IDE identifiers

The --ide command-line argument is accepted for every supported client, but the semantic token
legend is now identical for all of them: every client receives the same custom reqnroll.* token
types and maps them to colours client-side.  These scenarios assert the server starts and serves
that shared legend for each known IDE identifier (and an unknown one), exercising the
--ide startup plumbing through the real startup path.

Scenario Outline: The server starts and advertises the shared custom legend for each IDE identifier
	Given the LSP server is started for IDE "<ide>"
	Then the server advertises a semantic tokens provider
	And the semantic tokens legend includes the token types
		| tokenType               |
		| reqnroll.keyword        |
		| reqnroll.step_parameter |
	When the feature file "ide.feature" is opened with
		"""
		Feature: Addition

		Scenario: Add
			When I press add
		"""
	Then the semantic tokens include a "reqnroll.keyword" token for "When"

Examples:
	| ide          |
	| visualstudio |
	| vscode       |
	| rider        |
	| unknown-ide  |

# ── Per-IDE static capability advertisement ────────────────────────────────────
#
# textDocumentSync is gated on the client IDE identity because vscode-languageclient v10
# silently ignores dynamic client/registerCapability for textDocument/didChange when the
# static textDocumentSync is absent from the InitializeResult, so non-VS clients need the
# static entry. VS already handles dynamic-only textDocumentSync registration correctly.
#
# renameProvider (issue #33), unlike textDocumentSync, is advertised to every client
# including VS: VS's native F2 now wires directly to textDocument/prepareRename +
# textDocument/rename for the common (single, unambiguous binding) case. VS's custom
# "Reqnroll: Rename Step" command remains available alongside it for the multi-attribute
# disambiguation case standard LSP rename has no protocol-level way to prompt for.

Scenario Outline: Non-VS clients receive static textDocumentSync capability
	Given the LSP server is started for IDE "<ide>"
	Then the server statically advertises textDocumentSync with full sync and openClose

Examples:
	| ide     |
	| vscode  |
	| rider   |

# Note: a matching "VS client does not receive textDocumentSync" scenario is intentionally omitted.
# The in-process OmniSharp spec client merges dynamic client/registerCapability into ServerSettings,
# making static vs. dynamic textDocumentSync indistinguishable from the client side. The VS inspector
# logs confirm the static entry is absent in the real wire protocol. The behavioral coverage for VS
# textDocument/didChange is provided by the Handshake + DocumentLifecycle specs.

Scenario Outline: All clients receive static renameProvider capability
	Given the LSP server is started for IDE "<ide>"
	Then the server advertises renameProvider with prepareProvider

Examples:
	| ide          |
	| visualstudio |
	| vscode       |
	| rider        |

# ── Static inlayHint / foldingRange capability advertisement ───────────────────
#
# Unlike textDocumentSync/renameProvider, these are declared statically for every client
# (including VS), because the problem isn't a per-client protocol quirk — it's a race between
# vscode-languageclient's dynamic client/registerCapability round trip and VS Code's restore of
# previously-open .feature tabs on window load. If the tab renders first, VS Code never re-checks
# for a provider for the rest of the session, and no amount of closing/reopening the file recovers
# it. A statically-declared capability is known to the client the instant initialize resolves, so
# there's no later round trip to lose.

Scenario Outline: All clients receive static inlayHint and foldingRange capabilities
	Given the LSP server is started for IDE "<ide>"
	Then the server statically advertises an inlayHintProvider
	And the server statically advertises a foldingRangeProvider

Examples:
	| ide          |
	| visualstudio |
	| vscode       |
	| rider        |
	| unknown-ide  |

# ── LSP-server outcome pipeline capability advertisement ───────────────────────
#
# reqnrollTestOutcomesProvider is a typed top-level sibling of the spec's own capability fields
# (not nested under the generic `experimental` bucket), written via ServerCapabilities.ExtensionData
# because InitializeResult.Capabilities is init-only and can't be swapped for a subclass. Advertised
# to every client alike -- the feature has no per-IDE variation.

Scenario Outline: All clients receive the testOutcomes provider capability
	Given the LSP server is started for IDE "<ide>"
	Then the server advertises a testOutcomes provider capability

Examples:
	| ide          |
	| visualstudio |
	| vscode       |
	| rider        |
	| unknown-ide  |

# ── Custom protocol surface manifest ────────────────────────────────────────────
#
# Every reqnroll/* method registered via manual OnRequest/OnNotification routing in
# InitializeCustomProtocolRouting bypasses OmniSharp's AddHandler-driven dynamic capability
# registration, so without an explicit entry here the initialize response says nothing about
# them. ApplyCustomProtocolCapabilities (Program.cs) advertises each one as a typed top-level
# entry in ServerCapabilities.ExtensionData -- documentation of the full custom protocol surface
# for a developer reading the handshake, and (via this scenario) a regression guard that a
# handler wasn't silently dropped from InitializeCustomProtocolRouting. This is advertised to
# every client alike; none of it varies per IDE.

Scenario Outline: All clients receive the custom protocol capability manifest
	Given the LSP server is started for IDE "<ide>"
	Then the server advertises the following custom protocol capabilities
		| capability                                  | field                     | method                                    |
		| reqnrollWorkspaceLifecycleProvider           | projectLoadedMethod       | reqnroll/projectLoaded                    |
		| reqnrollWorkspaceLifecycleProvider           | projectUnloadedMethod     | reqnroll/projectUnloaded                  |
		| reqnrollWorkspaceLifecycleProvider           | projectFilesMethod        | reqnroll/projectFiles                     |
		| reqnrollFindStepUsagesProvider               | method                    | reqnroll/findStepUsages                   |
		| reqnrollGoToHooksProvider                    | method                    | reqnroll/goToHooks                        |
		| reqnrollGoToMatchingScenariosProvider        | method                    | reqnroll/goToMatchingScenarios            |
		| reqnrollResolveTestTargetsProvider           | method                    | reqnroll/resolveTestTargets               |
		| reqnrollFindUnusedStepDefinitionsProvider    | method                    | reqnroll/findUnusedStepDefinitions        |
		| reqnrollStepRenameProvider                   | renameTargetsMethod       | reqnroll/renameTargets                    |
		| reqnrollStepRenameProvider                   | selectRenameTargetMethod  | reqnroll/selectRenameTarget               |
		| reqnrollStepRenameProvider                   | renameAppliedMethod       | reqnroll/renameApplied                    |
		| reqnrollRefreshCodeLensProvider              | method                    | reqnroll/refreshCodeLens                  |
		| reqnrollSemanticTokensPushProvider           | method                    | reqnroll/semanticTokens                   |
		| reqnrollDocumentSymbolHierarchicalProvider   | method                    | reqnroll/documentSymbolHierarchical       |
		| reqnrollDocumentActivatedProvider            | method                    | reqnroll/documentActivated                |

Examples:
	| ide          |
	| visualstudio |
	| vscode       |
	| rider        |
	| unknown-ide  |
