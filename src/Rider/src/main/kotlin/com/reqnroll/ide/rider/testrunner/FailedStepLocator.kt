package com.reqnroll.ide.rider.testrunner

import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.SymbolKind

/**
 * Maps a failed step reported by the LSP server's step-trace parser (issue #451) back to its line
 * in the `.feature` file, using the `textDocument/documentSymbol` tree — which already carries
 * Background and Scenario steps as `Field`-kind children (see `DocumentSymbolService.cs`) — so no
 * Gherkin parsing (and no dialect-specific keyword list) is needed on the Kotlin side.
 *
 * The trace's `failedStepIndex` is an execution index over every step Reqnroll traced, which is the
 * enclosing Background step(s) first and then the scenario's own steps. A stack trace cannot be
 * used for this: the generated code misattributes any failure to the scenario's last step
 * (design doc §6).
 */
internal object FailedStepLocator {
    /**
     * Returns the 0-based line of the failed step of the scenario whose header is on
     * [scenarioLine], or null when it can't be placed confidently (the index is out of range for the
     * steps the symbol tree has, which happens while the file is mid-edit, or no scenario starts on
     * that line). Falls back to an exact name match on [failedStepText] when [failedStepIndex] is null.
     */
    fun locate(
        symbols: List<DocumentSymbol>,
        scenarioLine: Int,
        failedStepIndex: Int?,
        failedStepText: String?,
    ): Int? {
        val steps = executionSteps(symbols, scenarioLine, emptyList()) ?: return null
        if (failedStepIndex != null) {
            return steps.getOrNull(failedStepIndex)?.range?.start?.line
        }
        if (failedStepText.isNullOrBlank()) return null
        val wanted = failedStepText.trim()
        return steps.firstOrNull { it.name.trim() == wanted }?.range?.start?.line
    }

    /** Background steps in scope (Feature-level, then the enclosing Rule's) followed by the scenario's own steps, or null if no scenario header is on [scenarioLine]. */
    private fun executionSteps(
        symbols: List<DocumentSymbol>,
        scenarioLine: Int,
        inheritedBackground: List<DocumentSymbol>,
    ): List<DocumentSymbol>? {
        val background = inheritedBackground + symbols
            .filter { it.kind == SymbolKind.Constructor }
            .flatMap { stepsOf(it) }

        for (symbol in symbols) {
            when (symbol.kind) {
                SymbolKind.Method ->
                    if (symbol.selectionRange?.start?.line == scenarioLine) return background + stepsOf(symbol)
                SymbolKind.Module, SymbolKind.Namespace ->
                    executionSteps(symbol.children.orEmpty(), scenarioLine, background)?.let { return it }
                else -> Unit
            }
        }
        return null
    }

    private fun stepsOf(container: DocumentSymbol): List<DocumentSymbol> =
        container.children.orEmpty().filter { it.kind == SymbolKind.Field }
}
