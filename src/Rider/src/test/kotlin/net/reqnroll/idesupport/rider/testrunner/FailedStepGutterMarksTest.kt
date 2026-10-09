package net.reqnroll.idesupport.rider.testrunner

import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.SymbolKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class FailedStepGutterMarksTest {
    private fun sym(name: String, kind: SymbolKind, line: Int, children: List<DocumentSymbol> = emptyList()) =
        DocumentSymbol(name, kind, Range(Position(line, 0), Position(line, 10)), Range(Position(line, 0), Position(line, 10)))
            .apply { this.children = children }

    private fun step(text: String, line: Int) = sym(text, SymbolKind.Field, line)

    // Feature (line 0) > Background (2): steps 3 | Scenario (5): steps 6,7,8 | Rule (10) > Background (11): step 12 | Scenario (14): steps 15,16
    private val tree = listOf(
        sym(
            "F", SymbolKind.Module, 0, listOf(
                sym("Background", SymbolKind.Constructor, 2, listOf(step("Given feature bg ", 3))),
                sym("S1", SymbolKind.Method, 5, listOf(step("Given a ", 6), step("When b ", 7), step("Then c ", 8))),
                sym(
                    "R", SymbolKind.Namespace, 10, listOf(
                        sym("Background", SymbolKind.Constructor, 11, listOf(step("Given rule bg ", 12))),
                        sym("S2", SymbolKind.Method, 14, listOf(step("When d ", 15), step("Then e ", 16))),
                    )
                ),
            )
        )
    )

    @Test
    fun `locate counts Background steps before the scenario's own`() {
        assertEquals(7, FailedStepLocator.locate(tree, 5, failedStepIndex = 2, failedStepText = null))
        assertEquals(6, FailedStepLocator.locate(tree, 5, failedStepIndex = 1, failedStepText = null))
    }

    @Test
    fun `locate includes feature and rule backgrounds for a scenario inside a Rule`() {
        // execution order: feature bg(3), rule bg(12), When d(15), Then e(16)
        assertEquals(12, FailedStepLocator.locate(tree, 14, 1, null))
        assertEquals(16, FailedStepLocator.locate(tree, 14, 3, null))
    }

    @Test
    fun `locate returns null for an out-of-range index or an unknown scenario line`() {
        assertNull(FailedStepLocator.locate(tree, 5, 4, null))
        assertNull(FailedStepLocator.locate(tree, 99, 0, null))
    }

    @Test
    fun `locate falls back to an exact step-name match when the index is null`() {
        assertEquals(7, FailedStepLocator.locate(tree, 5, null, "When b"))
        assertNull(FailedStepLocator.locate(tree, 5, null, "When nope"))
        assertNull(FailedStepLocator.locate(tree, 5, null, null))
    }

    @Test
    fun `computeMarks marks the failed step of a failing scenario`() {
        val result = RunResult(
            RunOutcome.FAILED,
            listOf(RunResultRow("S1", RunOutcome.FAILED, "When b", failedStepIndex = 2, errorMessage = "boom\nstack")),
        )
        val marks = FailedStepGutterMarks.computeMarks(tree, 5, result)
        assertEquals(1, marks.size)
        assertEquals(7, marks[0].line)
        assertEquals("Failed - When b\nS1: boom", marks[0].tooltip)
    }

    @Test
    fun `computeMarks groups outline rows failing on the same step and separates different steps`() {
        val result = RunResult(
            RunOutcome.FAILED,
            listOf(
                RunResultRow("r1", RunOutcome.FAILED, "When b", failedStepIndex = 2),
                RunResultRow("r2", RunOutcome.FAILED, "When b", failedStepIndex = 2),
                RunResultRow("r3", RunOutcome.FAILED, "Then c", failedStepIndex = 3),
                RunResultRow("r4", RunOutcome.PASSED),
            ),
        )
        assertEquals(listOf(7, 8), FailedStepGutterMarks.computeMarks(tree, 5, result).map { it.line })
    }

    @Test
    fun `computeMarks yields nothing for a passing result or rows without a locatable step`() {
        assertTrue(FailedStepGutterMarks.computeMarks(tree, 5, RunResult(RunOutcome.PASSED)).isEmpty())
        val noDetail = RunResult(RunOutcome.FAILED, listOf(RunResultRow("S1", RunOutcome.FAILED)))
        assertTrue(FailedStepGutterMarks.computeMarks(tree, 5, noDetail).isEmpty())
    }
}
