package com.reqnroll.ide.rider.navigation

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.ex.AnActionListener
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger

/**
 * Diagnostic for #909: records which action actually runs when a navigation shortcut is pressed in
 * a `.feature` file, since Rider routes some of them through a backend-delegating wrapper whose
 * choice isn't otherwise visible. Logs only Go To / declaration related actions, only for
 * `.feature` editors.
 */
class ReqnrollActionDiagnostics : AnActionListener {
    override fun beforeActionPerformed(action: AnAction, event: AnActionEvent) {
        val id = ActionManager.getInstance().getId(action) ?: return
        if (!id.contains("Goto", ignoreCase = true) && !id.startsWith("Reqnroll.")) return
        val file = event.getData(CommonDataKeys.VIRTUAL_FILE) ?: return
        if (!isFeatureExtension(file.extension)) return

        ReqnrollDebugLogger.info(
            "action performed: id=$id class=${action.javaClass.name} place=${event.place} input=${event.inputEvent?.javaClass?.simpleName}")
    }
}
