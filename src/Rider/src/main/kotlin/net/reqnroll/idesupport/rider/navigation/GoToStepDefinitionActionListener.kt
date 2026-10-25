package net.reqnroll.idesupport.rider.navigation

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.ex.AnActionListener
import net.reqnroll.idesupport.rider.isFeatureExtension

/**
 * Arms [GoToStepDefinitionTelemetry] when the user runs a Go To Declaration action in a `.feature`
 * file. Covers every Rider version: on 2026.2+ [ReqnrollGoToDefinitionAction] also arms it, on
 * older versions the platform runs the action itself and only this listener sees it.
 */
internal class GoToStepDefinitionActionListener : AnActionListener {
    override fun beforeActionPerformed(action: AnAction, event: AnActionEvent) {
        val id = ActionManager.getInstance().getId(action)
        if (!isGoToDeclarationActionId(id)) return
        if (!isFeatureExtension(event.getData(CommonDataKeys.VIRTUAL_FILE)?.extension)) return
        GoToStepDefinitionTelemetry.arm()
    }

    companion object {
        internal fun isGoToDeclarationActionId(id: String?): Boolean =
            id == "GotoDeclaration" || id == "GotoDeclarationOnly" || id == "GotoDeclarationOrUsage"
    }
}
