package net.reqnroll.idesupport.rider.navigation

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class GoToStepDefinitionActionListenerTest {
    @Test
    fun `recognises the Go To Declaration action ids`() {
        listOf("GotoDeclaration", "GotoDeclarationOnly", "GotoDeclarationOrUsage").forEach {
            assertTrue(GoToStepDefinitionActionListener.isGoToDeclarationActionId(it), it)
        }
    }

    @Test
    fun `ignores other actions`() {
        assertFalse(GoToStepDefinitionActionListener.isGoToDeclarationActionId("GotoImplementation"))
        assertFalse(GoToStepDefinitionActionListener.isGoToDeclarationActionId(null))
    }
}
