package com.reqnroll.ide.rider.testrunner

import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.SymbolKind
import kotlin.test.Test
import kotlin.test.assertEquals

class RunLensSupportTest {
    private fun methodSymbol(name: String, line: Int, children: List<DocumentSymbol> = emptyList()) =
        DocumentSymbol(
            name, SymbolKind.Method,
            Range(Position(line, 0), Position(line + 1, 0)),
            Range(Position(line, 0), Position(line + 1, 0)),
        ).apply { this.children = children }

    private fun namespaceSymbol(name: String, children: List<DocumentSymbol>) =
        DocumentSymbol(
            name, SymbolKind.Namespace,
            Range(Position(0, 0), Position(10, 0)),
            Range(Position(0, 0), Position(10, 0)),
        ).apply { this.children = children }

    // ── collectMethodSymbols ─────────────────────────────────────────────────

    @Test
    fun `collectMethodSymbols collects a top-level Method symbol`() {
        val result = RunLensSupport.collectMethodSymbols(listOf(methodSymbol("Add two numbers", 1)))
        assertEquals(1, result.size)
        assertEquals("Add two numbers", result[0].name)
    }

    @Test
    fun `collectMethodSymbols descends into Rule (Namespace-kind) children to find nested scenarios`() {
        val scenario = methodSymbol("Nested scenario", 3)
        val rule = namespaceSymbol("My Rule", listOf(scenario))
        val result = RunLensSupport.collectMethodSymbols(listOf(rule))
        assertEquals(1, result.size)
        assertEquals("Nested scenario", result[0].name)
    }

    @Test
    fun `collectMethodSymbols ignores non-Method symbols at any depth`() {
        val step = DocumentSymbol(
            "Given a step", SymbolKind.Field,
            Range(Position(2, 0), Position(2, 1)),
            Range(Position(2, 0), Position(2, 1)),
        )
        val scenario = methodSymbol("S", 1, listOf(step))
        val result = RunLensSupport.collectMethodSymbols(listOf(scenario))
        assertEquals(1, result.size)
        assertEquals("S", result[0].name)
    }

    @Test
    fun `collectMethodSymbols returns an empty list for an empty tree`() {
        assertEquals(emptyList(), RunLensSupport.collectMethodSymbols(emptyList()))
    }

    // ── renderTitle ──────────────────────────────────────────────────────────

    @Test
    fun `renderTitle shows the play glyph when there is no cached result`() {
        assertEquals("▶ Run", RunLensSupport.renderTitle(null))
    }

    @Test
    fun `renderTitle shows the check glyph for a cached passing result`() {
        assertEquals("✓ Run", RunLensSupport.renderTitle(RunOutcome.PASSED))
    }

    @Test
    fun `renderTitle shows the cross glyph for a cached failing result`() {
        assertEquals("✗ Run", RunLensSupport.renderTitle(RunOutcome.FAILED))
    }

    // ── renderTooltip ────────────────────────────────────────────────────────

    @Test
    fun `renderTooltip falls back to the title when there is no result`() {
        assertEquals("▶ Run", RunLensSupport.renderTooltip("▶ Run", null))
    }

    @Test
    fun `renderTooltip falls back to the title for a TRX-sourced result with no row detail`() {
        assertEquals("✗ Run", RunLensSupport.renderTooltip("✗ Run", RunResult(RunOutcome.FAILED)))
    }

    @Test
    fun `renderTooltip falls back to the title when every row passed`() {
        val result = RunResult(RunOutcome.PASSED, listOf(RunResultRow("row 1", RunOutcome.PASSED)))
        assertEquals("✓ Run", RunLensSupport.renderTooltip("✓ Run", result))
    }

    @Test
    fun `renderTooltip lists only the failed rows, with their failed-step text when known`() {
        val result = RunResult(
            RunOutcome.FAILED,
            listOf(
                RunResultRow("row 1", RunOutcome.PASSED),
                RunResultRow("row 2", RunOutcome.FAILED, failedStepText = "When the calculation explodes"),
                RunResultRow("row 3", RunOutcome.FAILED),
            ),
        )
        assertEquals(
            "row 2: When the calculation explodes\nrow 3",
            RunLensSupport.renderTooltip("✗ Run", result),
        )
    }
}
