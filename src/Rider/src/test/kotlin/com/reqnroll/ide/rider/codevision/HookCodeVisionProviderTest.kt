package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class HookCodeVisionProviderTest {
    // Regression test for a real bug: two lenses anchored on the same Scenario: line (the
    // scenario-only "N hooks" lens and the consolidated step-hooks lens) both resolved to the
    // exact same TextRange, so IntelliJ's CodeVision only rendered/wired up one of them.

    @Test
    fun `dedupedOffset returns the plain display offset for the first entry on a line`() {
        val offset = HookCodeVisionProvider.dedupedOffset(
            lineStartOffset = 100, displayCharacter = 0, priorEntriesOnLine = 0, lineEndOffset = 120,
        )

        assertEquals(100, offset)
    }

    @Test
    fun `dedupedOffset nudges subsequent entries on the same line so ranges stay distinct`() {
        val first = HookCodeVisionProvider.dedupedOffset(
            lineStartOffset = 100, displayCharacter = 0, priorEntriesOnLine = 0, lineEndOffset = 120,
        )
        val second = HookCodeVisionProvider.dedupedOffset(
            lineStartOffset = 100, displayCharacter = 0, priorEntriesOnLine = 1, lineEndOffset = 120,
        )

        assertEquals(100, first)
        assertEquals(101, second)
    }

    @Test
    fun `dedupedOffset clamps to the line end so the nudge never spills onto the next line`() {
        val offset = HookCodeVisionProvider.dedupedOffset(
            lineStartOffset = 100, displayCharacter = 0, priorEntriesOnLine = 50, lineEndOffset = 103,
        )

        assertEquals(103, offset)
    }

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
}
