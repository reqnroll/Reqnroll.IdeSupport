package com.reqnroll.ide.rider.codevision

import kotlin.test.Test
import kotlin.test.assertEquals

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
}
