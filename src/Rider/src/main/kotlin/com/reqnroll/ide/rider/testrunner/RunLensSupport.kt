package com.reqnroll.ide.rider.testrunner

import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.ui.model.ClickableTextCodeVisionEntry
import com.intellij.openapi.editor.Document
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.codevision.StepUsagesCodeVisionProvider
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.SymbolKind

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
     */
    fun computeEntries(
        project: Project,
        document: Document,
        filePath: String,
        providerId: String,
    ): List<Pair<TextRange, CodeVisionEntry>> {
        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(filePath))
        val symbols = ReqnrollRequestSender.documentSymbol(project, uri) ?: return emptyList()
        val scenarioSymbols = collectMethodSymbols(symbols)
        if (scenarioSymbols.isEmpty()) return emptyList()

        val result = mutableListOf<Pair<TextRange, CodeVisionEntry>>()
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

            val offset = document.getLineStartOffset(startLine)
            val entry = buildEntry(project, providerId, uri, startLine, targets)
            result.add(TextRange(offset, offset) to entry)
        }
        return result
    }

    /**
     * Builds the CodeVision entry for one resolved scenario — title reflects the cached last-run
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
    ): ClickableTextCodeVisionEntry {
        val title = renderTitle(RunTestResultStore.get(uri, startLine)?.outcome)
        return StepUsagesCodeVisionProvider.buildEntry(title, providerId) {
            RunTestRunner.run(project, uri, startLine, targets)
        }
    }

    /** `internal` so it's unit-testable without a platform fixture. */
    internal fun renderTitle(outcome: RunOutcome?): String = when (outcome) {
        null -> "▶ Run"
        RunOutcome.PASSED -> "✓ Run"
        RunOutcome.FAILED -> "✗ Run"
    }
}
