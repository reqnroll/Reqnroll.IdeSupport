package com.reqnroll.ide.rider.testrunner

import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.ui.model.ClickableTextCodeVisionEntry
import com.intellij.openapi.editor.Document
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.reqnroll.ide.rider.codevision.StepUsagesCodeVisionProvider
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.SymbolKind
import com.reqnroll.ide.rider.lsp.localPathToLspUri

/**
 * Shared lens-computation logic for [RunTestCodeVisionProvider] — mirrors
 * [com.reqnroll.ide.rider.codevision.HookLensSupport]'s shape (design doc §5/§6, issue #262):
 * fetch scenario/Outline ranges via the standard `textDocument/documentSymbol` request (already
 * used by the Structure View — see [com.reqnroll.ide.rider.lsp.ReqnrollRequestSender.documentSymbol]),
 * then resolve each one's generated test method(s) via the new custom `reqnroll/resolveTestTargets`
 * request. No PSI is used anywhere in this chain — see [RunTestCodeVisionProvider]'s doc comment for
 * why `RunLineMarkerContributor` (the design doc's original recommendation) isn't viable here.
 */
internal object RunLensSupport {
    /**
     * Recursively collects every `SymbolKind.Method` node (Scenario/Scenario Outline — see
     * `DocumentSymbolHandler.cs`'s `ToSymbolKind`) from a document symbol tree, at any nesting
     * depth. Needed because a scenario nested under a `Rule` (kind `Namespace`) only shows up as a
     * grandchild of the top-level list, not a direct child. `internal` so it's unit-testable
     * without a platform fixture.
     */
    internal fun collectMethodSymbols(symbols: List<DocumentSymbol>): List<DocumentSymbol> {
        val result = mutableListOf<DocumentSymbol>()
        for (symbol in symbols) {
            if (symbol.kind == SymbolKind.Method) result.add(symbol)
            val children = symbol.children
            if (!children.isNullOrEmpty()) result.addAll(collectMethodSymbols(children))
        }
        return result
    }

    /**
     * Recursively collects every `SymbolKind.Module` (Feature) and `SymbolKind.Namespace` (Rule)
     * node from a document symbol tree, at any nesting depth — the containers a "Run Scenarios"
     * lens renders on (see `DocumentSymbolHandler.cs`'s `ToSymbolKind`, the same mapping VS's
     * `RunTestCodeLensService.CollectContainerNodes` uses). Mirror of [collectMethodSymbols]'s
     * shape for the container half of the run lens. `internal` so it's unit-testable without a
     * platform fixture.
     */
    internal fun collectContainerSymbols(symbols: List<DocumentSymbol>): List<DocumentSymbol> {
        val result = mutableListOf<DocumentSymbol>()
        for (symbol in symbols) {
            if (symbol.kind == SymbolKind.Module || symbol.kind == SymbolKind.Namespace) result.add(symbol)
            val children = symbol.children
            if (!children.isNullOrEmpty()) result.addAll(collectContainerSymbols(children))
        }
        return result
    }

    /**
     * Fetches the scenario/Outline symbols for [filePath]'s `.feature` document and builds one
     * CodeVision entry per scenario that has at least one resolved test target — scenarios with
     * none (not built yet, or a naming-rule mismatch) get no entry at all, matching the "not built
     * yet" reasoning already used in the VS Code/VS implementations of this same feature.
     *
     * The symbol-tree walk itself runs on every call — `computeCodeVision` is invoked by IntelliJ's
     * platform on its own schedule (edits, file open, etc.) with no way for this plugin to ask for
     * only the visible range (issue #495's platform survey). What's skipped per call is the
     * `reqnroll/resolveTestTargets` RPC: [RunTestTargetCache] reuses the previous resolution for any
     * scenario whose identity (kind + name) hasn't changed since the last walk, so a large feature
     * file only pays the RPC cost for scenarios that actually changed, not the whole document every
     * time.
     *
     * Feature/Rule header lines additionally get a "Run Scenarios" entry (issue #744): the same
     * walk collects Module-kind (Feature) and Namespace-kind (Rule) nodes (including Rules nested
     * inside Features) and resolves each one's whole contained scenario set in a single
     * `reqnroll/resolveContainerTestTargets` call, cached under the same (uri, line) identity rule
     * as scenarios.
     */
    fun computeEntries(
        project: Project,
        document: Document,
        filePath: String,
        providerId: String,
    ): List<Pair<TextRange, CodeVisionEntry>> {
        val uri = localPathToLspUri(filePath)
        val symbols = ReqnrollRequestSender.documentSymbol(project, uri) ?: return emptyList()
        val scenarioSymbols = collectMethodSymbols(symbols)
        val containerSymbols = collectContainerSymbols(symbols)
        if (scenarioSymbols.isEmpty() && containerSymbols.isEmpty()) return emptyList()

        val result = mutableListOf<Pair<TextRange, CodeVisionEntry>>()
        val resolvedScenarios = mutableListOf<ScenarioRunTarget>()
        for (symbol in scenarioSymbols) {
            val selectionRange = symbol.selectionRange ?: continue
            val startLine = selectionRange.start.line
            if (startLine < 0 || startLine >= document.lineCount) continue

            val identity = "${symbol.detail}|${symbol.name}"
            val targets = RunTestTargetCache.get(uri, startLine, identity) ?: run {
                val response = ReqnrollRequestSender.resolveTestTargets(
                    project, uri,
                    selectionRange.start.line, selectionRange.start.character,
                    selectionRange.end.line, selectionRange.end.character,
                )
                val resolved = response?.targets.orEmpty()
                RunTestTargetCache.put(uri, startLine, identity, resolved)
                resolved
            }
            if (targets.isEmpty()) continue
            resolvedScenarios.add(ScenarioRunTarget(startLine, targets))

            val offset = document.getLineStartOffset(startLine)
            val entry = buildEntry(project, providerId, uri, startLine, targets)
            result.add(TextRange(offset, offset) to entry)
        }

        // "Run Scenarios" lenses on Feature/Rule header lines (issue #744): resolve every
        // contained scenario in one container request per block. symbol.range (the block's full
        // body) goes to the server — the same choice VS's RunTestCodeLensService makes, since the
        // handler resolves every scenario/Outline tag fully contained within the range given —
        // while the lens itself anchors on the header line (symbol.selectionRange).
        for (symbol in containerSymbols) {
            val selectionRange = symbol.selectionRange ?: continue
            val startLine = selectionRange.start.line
            if (startLine < 0 || startLine >= document.lineCount) continue

            val range = symbol.range ?: continue
            // Kind stands in for Detail, which server-side Feature/Rule symbols never set (same
            // reasoning as VS's `container:{Kind}|{Name}` lens key) — prefixed so a container's
            // identity can never collide with a scenario's `detail|name` at the same line.
            val identity = "container:${symbol.kind}|${symbol.name}"
            val targets = RunTestTargetCache.get(uri, startLine, identity) ?: run {
                val response = ReqnrollRequestSender.resolveContainerTestTargets(
                    project, uri,
                    range.start.line, range.start.character,
                    range.end.line, range.end.character,
                )
                val resolved = response?.targets.orEmpty()
                RunTestTargetCache.put(uri, startLine, identity, resolved)
                resolved
            }
            if (targets.isEmpty()) continue

            val offset = document.getLineStartOffset(startLine)
            // The scenarios lying inside this block, so a run can also update each scenario's own lens.
            val contained = resolvedScenarios.filter { it.startLine in range.start.line..range.end.line }
            val entry = buildEntry(project, providerId, uri, startLine, targets, isContainer = true, scenarios = contained)
            result.add(TextRange(offset, offset) to entry)
        }
        return result
    }

    /**
     * Builds the CodeVision entry for one resolved scenario — or, with [isContainer], the
     * "Run Scenarios" entry for a whole Feature/Rule block — title reflects the cached last-run
     * outcome (if any) in [RunTestResultStore], mirroring VS Code's `$(check)`/`$(error)` icon-swap
     * idea with plain glyphs (Rider's lens text has no codicon syntax). `internal` so it's
     * unit-testable without a platform fixture.
     */
    internal fun buildEntry(
        project: Project,
        providerId: String,
        uri: String,
        startLine: Int,
        targets: List<ScenarioTestTargetItem>,
        isContainer: Boolean = false,
        scenarios: List<ScenarioRunTarget> = emptyList(),
    ): ClickableTextCodeVisionEntry {
        val lastResult = RunTestResultStore.get(uri, startLine)
        val title = renderTitle(lastResult?.outcome, isContainer)
        val tooltip = renderTooltip(title, lastResult)
        return StepUsagesCodeVisionProvider.buildEntry(title, providerId, tooltip) {
            RunTestRunner.run(project, uri, startLine, targets, scenarios)
        }
    }

    /**
     * Renders the lens title — the singular "Run" for a scenario, the plural "Run Scenarios" for a
     * Feature/Rule container (issue #744), each prefixed with the cached last-run outcome glyph the
     * same way. `internal` so it's unit-testable without a platform fixture.
     */
    internal fun renderTitle(outcome: RunOutcome?, isContainer: Boolean = false): String {
        val suffix = if (isContainer) " Scenarios" else ""
        return when (outcome) {
            null -> "▶ Run$suffix"
            RunOutcome.PASSED -> "✓ Run$suffix"
            RunOutcome.FAILED -> "✗ Run$suffix"
        }
    }

    /**
     * Summarizes the failed row(s) from [result]'s per-row detail (LSP-server outcome pipeline,
     * #700/#702) for the lens tooltip — the plain [title] alone doesn't say *which* Scenario
     * Outline row failed or on what step. Falls back to [title] verbatim when [result] has no rows
     * (a TRX-sourced fallback result, or no run yet), same as before this detail existed.
     * `internal` so it's unit-testable without a platform fixture.
     */
    internal fun renderTooltip(title: String, result: RunResult?): String {
        val failedRows = result?.rows?.filter { it.outcome == RunOutcome.FAILED } ?: emptyList()
        if (failedRows.isEmpty()) return title
        return failedRows.joinToString("\n") { row ->
            if (row.failedStepText != null) "${row.displayName}: ${row.failedStepText}" else row.displayName
        }
    }
}
