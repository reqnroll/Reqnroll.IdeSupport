package net.reqnroll.idesupport.rider.debugger

import com.intellij.openapi.actionSystem.ActionPromoter
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.DataContext

/**
 * Puts [StepIntoStepDefinitionAction] ahead of the platform's Step Into, which it shares shortcuts with, while the
 * debugger is stopped on a `.feature` line. Anywhere else the promoter does nothing and the action is disabled, so
 * the shortcut runs the platform's Step Into.
 */
class StepIntoStepDefinitionPromoter : ActionPromoter {
    override fun promote(actions: List<AnAction>, context: DataContext): List<AnAction> {
        val ours = actions.filterIsInstance<StepIntoStepDefinitionAction>()
        if (ours.isEmpty()) return emptyList()
        val session = StepIntoStepDefinitionAction.sessionOf(context) ?: return emptyList()
        return if (StepIntoStepDefinitionAction.isStoppedOnFeatureLine(session)) ours else emptyList()
    }
}
