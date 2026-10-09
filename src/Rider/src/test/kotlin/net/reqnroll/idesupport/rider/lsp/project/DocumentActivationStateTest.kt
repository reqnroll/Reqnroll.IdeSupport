package net.reqnroll.idesupport.rider.lsp.project

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * Mirrors the phase transitions documented in the ported source,
 * src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspInterception/DocumentActivationState.cs.
 */
class DocumentActivationStateTest {
    @Test
    fun `didOpen then activation is the common case - sends once`() {
        val state = DocumentActivationState()
        assertEquals(DocumentActivationAction.NONE, state.onDidOpen("a.feature"))
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))
    }

    @Test
    fun `repeated activation after the first send is a no-op`() {
        val state = DocumentActivationState()
        state.onDidOpen("a.feature")
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))
        assertEquals(DocumentActivationAction.NONE, state.onWindowActivated("a.feature"))
        assertEquals(DocumentActivationAction.NONE, state.onWindowActivated("a.feature"))
    }

    @Test
    fun `activation before open - restored tab already active at solution load, issue 85`() {
        val state = DocumentActivationState()
        // WindowActivated arrives first: nothing to send yet, but remembered as pending.
        assertEquals(DocumentActivationAction.NONE, state.onWindowActivated("a.feature"))
        // didOpen catches up: the pending activation now fires.
        assertEquals(DocumentActivationAction.SEND_NOW, state.onDidOpen("a.feature"))
    }

    @Test
    fun `close then reopen resets the state for a fresh open-lifetime`() {
        val state = DocumentActivationState()
        state.onDidOpen("a.feature")
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))

        state.onDidClose("a.feature")

        // Same file reopened: must be treated as NotSeen again, not "already activated".
        assertEquals(DocumentActivationAction.NONE, state.onDidOpen("a.feature"))
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))
    }

    @Test
    fun `didOpen firing again without an intervening didClose resets to Opened`() {
        val state = DocumentActivationState()
        state.onDidOpen("a.feature")
        state.onWindowActivated("a.feature") // now Activated

        // Unexpected re-open with no didClose in between: not this class's job to diagnose,
        // but it must still produce one more activation rather than staying stuck.
        assertEquals(DocumentActivationAction.NONE, state.onDidOpen("a.feature"))
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))
    }

    @Test
    fun `each file path is tracked independently`() {
        val state = DocumentActivationState()
        state.onDidOpen("a.feature")
        state.onDidOpen("b.feature")

        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("a.feature"))
        assertEquals(DocumentActivationAction.SEND_NOW, state.onWindowActivated("b.feature"))
        assertEquals(DocumentActivationAction.NONE, state.onWindowActivated("a.feature"))
    }
}
