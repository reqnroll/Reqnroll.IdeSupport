package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class HookCodeVisionProviderTest {
    // Regression tests for a real bug: command.arguments elements from a live LSP4J response
    // turned out to be raw JsonPrimitive, not plain Number/Boolean — a direct `as? Number`/
    // `as? Boolean` cast silently failed every time, so every hook lens click fell back to the
    // display position/ownLevelOnly=false instead of the server-sent click target, making every
    // lens navigate identically regardless of which one was clicked.

    @Test
    fun `argAsInt reads a plain boxed Number`() {
        assertEquals(3, HookCodeVisionProvider.argAsInt(listOf("uri", 3, 0, true), index = 1))
    }

    @Test
    fun `argAsInt reads a JsonPrimitive number, the actual runtime shape LSP4J produces`() {
        assertEquals(3, HookCodeVisionProvider.argAsInt(listOf("uri", JsonPrimitive(3), JsonPrimitive(0)), index = 1))
    }

    @Test
    fun `argAsInt returns null for a missing or out-of-range index`() {
        assertNull(HookCodeVisionProvider.argAsInt(listOf("uri"), index = 1))
        assertNull(HookCodeVisionProvider.argAsInt(null, index = 1))
    }

    @Test
    fun `argAsBoolean reads a plain boxed Boolean`() {
        assertEquals(true, HookCodeVisionProvider.argAsBoolean(listOf("uri", 3, 0, true), index = 3))
    }

    @Test
    fun `argAsBoolean reads a JsonPrimitive boolean, the actual runtime shape LSP4J produces`() {
        assertEquals(true, HookCodeVisionProvider.argAsBoolean(listOf("uri", JsonPrimitive(true)), index = 1))
    }

    @Test
    fun `argAsBoolean returns null for a missing index`() {
        assertNull(HookCodeVisionProvider.argAsBoolean(listOf("uri"), index = 3))
        assertNull(HookCodeVisionProvider.argAsBoolean(null, index = 3))
    }

    // Regression tests for a real platform limitation (not a coding bug): IntelliJ's CodeVision
    // only renders one chip per line per provider, so two lenses anchored on the same Scenario:
    // line (scenario-only + consolidated step-hooks) must be merged into a single entry.

    @Test
    fun `combinedTitle joins every lens title on a line with a middle dot`() {
        assertEquals("2 hooks · 1 step hook", HookCodeVisionProvider.combinedTitle(listOf("2 hooks", "1 step hook")))
    }

    @Test
    fun `combinedTitle passes a single title through unchanged`() {
        assertEquals("1 hook", HookCodeVisionProvider.combinedTitle(listOf("1 hook")))
    }

    @Test
    fun `richestClick picks the candidate with the highest line number`() {
        // The step-hooks lens's click target (the scenario's first step, a later line) should
        // win over the scenario-only lens's own (earlier) line, since GoToHooksHandler's
        // cumulative sets are hierarchical — querying at the deepest position returns every hook
        // implied by the combined title.
        val result = HookCodeVisionProvider.richestClick(listOf(2 to 0, 5 to 0))

        assertEquals(5 to 0, result)
    }

    @Test
    fun `richestClick returns the only candidate when there is just one lens on the line`() {
        assertEquals(2 to 4, HookCodeVisionProvider.richestClick(listOf(2 to 4)))
    }
}
