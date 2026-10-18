package net.reqnroll.idesupport.rider.formatting

import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.AnActionResult
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.ex.AnActionListener
import com.intellij.openapi.actionSystem.ActionManager

/**
 * Feeds [ExplicitReformatTracker] when the user invokes Reformat Code (`ReformatCode`) or the Reformat
 * File dialog (`ShowReformatFileDialog`) — the two explicit entry points; confirmed against Rider
 * 2024.3.5's `LangActions.xml`. Format on save never goes through either action.
 *
 * Marks both before and after the action: the Reformat File dialog runs modally inside
 * `actionPerformed`, so only the second mark is fresh when the reformat actually starts.
 */
internal class ExplicitReformatActionListener : AnActionListener {
    override fun beforeActionPerformed(action: AnAction, event: AnActionEvent) = markIfReformat(action, event)

    override fun afterActionPerformed(action: AnAction, event: AnActionEvent, result: AnActionResult) =
        markIfReformat(action, event)

    private fun markIfReformat(action: AnAction, event: AnActionEvent) {
        val id = ActionManager.getInstance().getId(action)
        if (!isExplicitReformatActionId(id)) return
        val files = event.getData(CommonDataKeys.VIRTUAL_FILE_ARRAY)?.toList()
            ?: listOfNotNull(event.getData(CommonDataKeys.VIRTUAL_FILE))
        ExplicitReformatTracker.instance.mark(files.map { it.path })
    }

    companion object {
        internal fun isExplicitReformatActionId(id: String?): Boolean =
            id == "ReformatCode" || id == "ShowReformatFileDialog"
    }
}
