package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.Command
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class HookCodeVisionProviderTest {
    // Regression tests for a real bug: command.arguments elements from a live LSP4J response
    // turned out to be raw JsonPrimitive, not plain Number/Boolean — a direct `as? Number`/
    // `as? Boolean` cast silently failed every time, so every hook lens click fell back to the
    // display position/ownLevelOnly=false instead of the server-sent click target, making every
    // lens navigate identically regardless of which one was clicked.

    @Test
    fun `argAsInt reads a plain boxed Number`() {
        assertEquals(3, HookLensSupport.argAsInt(listOf("uri", 3, 0, true), index = 1))
    }

    @Test
    fun `argAsInt reads a JsonPrimitive number, the actual runtime shape LSP4J produces`() {
        assertEquals(3, HookLensSupport.argAsInt(listOf("uri", JsonPrimitive(3), JsonPrimitive(0)), index = 1))
    }

    @Test
    fun `argAsInt returns null for a missing or out-of-range index`() {
        assertNull(HookLensSupport.argAsInt(listOf("uri"), index = 1))
        assertNull(HookLensSupport.argAsInt(null, index = 1))
    }

    @Test
    fun `argAsBoolean reads a plain boxed Boolean`() {
        assertEquals(true, HookLensSupport.argAsBoolean(listOf("uri", 3, 0, true), index = 3))
    }

    @Test
    fun `argAsBoolean reads a JsonPrimitive boolean, the actual runtime shape LSP4J produces`() {
        assertEquals(true, HookLensSupport.argAsBoolean(listOf("uri", JsonPrimitive(true)), index = 1))
    }

    @Test
    fun `argAsBoolean returns null for a missing index`() {
        assertNull(HookLensSupport.argAsBoolean(listOf("uri"), index = 3))
        assertNull(HookLensSupport.argAsBoolean(null, index = 3))
    }

    // Regression tests for a real platform limitation (not a coding bug, confirmed by decompiling
    // IntelliJ's CodeVision engine and testing empirically): a single provider can't reliably show
    // two entries on the same Scenario: line, so the Scenario-only and consolidated step-hooks
    // lenses are split across HookCodeVisionProvider/StepHooksCodeVisionProvider — isStepHooksLens
    // is how they tell which lens is theirs from the shared textDocument/codeLens response.

    private fun lensWithArgs(args: List<Any>?) =
        CodeLens(Range(Position(1, 0), Position(1, 0)), Command("title", "reqnroll.goToHooks", args), null)

    @Test
    fun `isStepHooksLens is true when the 5th argument is true`() {
        assertTrue(HookLensSupport.isStepHooksLens(lensWithArgs(listOf("uri", 1, 0, true, true))))
    }

    @Test
    fun `isStepHooksLens is false when the 5th argument is absent (own-level lens)`() {
        assertFalse(HookLensSupport.isStepHooksLens(lensWithArgs(listOf("uri", 1, 0, true))))
    }

    @Test
    fun `isStepHooksLens is false when there are no arguments at all`() {
        assertFalse(HookLensSupport.isStepHooksLens(lensWithArgs(null)))
    }
}
