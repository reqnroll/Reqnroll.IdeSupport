package com.reqnroll.ide.rider.breadcrumbs

import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.SymbolKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ReqnrollFeatureBreadcrumbsCollectorTest {
    // Ranges are expressed on a single line, so treating Position.character as the offset directly
    // (ignoring line) is a faithful, Document-free stand-in for offsetOf in these tests.
    private val offsetOf: (Position) -> Int = { it.character }

    private fun symbol(name: String, start: Int, end: Int, children: List<DocumentSymbol> = emptyList()): DocumentSymbol {
        val range = Range(Position(0, start), Position(0, end))
        return DocumentSymbol(name, SymbolKind.Class, range, range).also { it.children = children }
    }

    @Test
    fun `returns a single-level trail when the offset matches only a top-level symbol`() {
        val feature = symbol("Feature: A", 0, 100)

        val trail = selectCrumbTrail(listOf(feature), offset = 50, offsetOf)

        assertEquals(listOf(feature), trail)
    }

    @Test
    fun `descends through nested children whose ranges contain the offset`() {
        val step = symbol("Given I do a thing", 20, 40)
        val scenario = symbol("Scenario: S", 10, 90, children = listOf(step))
        val feature = symbol("Feature: F", 0, 100, children = listOf(scenario))

        val trail = selectCrumbTrail(listOf(feature), offset = 25, offsetOf)

        assertEquals(listOf(feature, scenario, step), trail)
    }

    @Test
    fun `stops descending once no child at the current level contains the offset`() {
        val step = symbol("Given I do a thing", 20, 40)
        val scenario = symbol("Scenario: S", 10, 90, children = listOf(step))
        val feature = symbol("Feature: F", 0, 100, children = listOf(scenario))

        // Inside the scenario's range but outside the step's -- the trail should include the
        // scenario but not descend into the step.
        val trail = selectCrumbTrail(listOf(feature), offset = 60, offsetOf)

        assertEquals(listOf(feature, scenario), trail)
    }

    @Test
    fun `returns an empty trail when the offset is outside every top-level symbol's range`() {
        val feature = symbol("Feature: F", 0, 100)

        val trail = selectCrumbTrail(listOf(feature), offset = 200, offsetOf)

        assertTrue(trail.isEmpty())
    }

    @Test
    fun `includes the boundary offset (inclusive range)`() {
        val feature = symbol("Feature: F", 0, 100)

        val trail = selectCrumbTrail(listOf(feature), offset = 100, offsetOf)

        assertEquals(listOf(feature), trail)
    }
}
