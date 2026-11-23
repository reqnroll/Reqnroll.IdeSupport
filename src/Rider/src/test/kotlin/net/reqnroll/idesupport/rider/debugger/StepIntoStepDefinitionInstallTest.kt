package net.reqnroll.idesupport.rider.debugger

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.testFramework.ApplicationRule
import com.intellij.testFramework.runInEdtAndWait
import org.junit.ClassRule
import org.junit.Test
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertSame

/**
 * Checks [StepIntoStepDefinitionAction.install] against the test application's real action manager: it wraps
 * whatever is registered as Step Into, keeps that action as its delegate, wraps only once, and uninstall puts the
 * original back.
 */
class StepIntoStepDefinitionInstallTest {
    companion object {
        @JvmField
        @ClassRule
        val applicationRule = ApplicationRule()
    }

    @Test
    fun `install wraps the registered Step Into once and uninstall restores it`() {
        val manager = ActionManager.getInstance()
        val original = assertNotNull(manager.getAction(StepIntoStepDefinitionAction.STEP_INTO_ACTION_ID))
        try {
            runInEdtAndWait {
                StepIntoStepDefinitionAction.install()
                StepIntoStepDefinitionAction.install()
            }

            val installed = assertIs<StepIntoStepDefinitionAction>(manager.getAction(StepIntoStepDefinitionAction.STEP_INTO_ACTION_ID))
            assertSame(original, installed.delegate)

            runInEdtAndWait { StepIntoStepDefinitionAction.uninstall() }
            assertSame(original, manager.getAction(StepIntoStepDefinitionAction.STEP_INTO_ACTION_ID))
        } finally {
            runInEdtAndWait { manager.replaceAction(StepIntoStepDefinitionAction.STEP_INTO_ACTION_ID, original) }
        }
    }
}
