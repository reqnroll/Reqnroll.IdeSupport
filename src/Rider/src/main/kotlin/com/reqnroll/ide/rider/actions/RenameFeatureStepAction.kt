package com.reqnroll.ide.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.reqnroll.ide.rider.lsp.localPathToLspUri

/**
 * Rename Step, `.feature`-file side (issue #160) — the Rider-side surface for the
 * `reqnroll/renameTargets` disambiguation + standard `textDocument/rename` flow. Bound to
 * Shift+F6: nothing else claims that shortcut for `.feature` files (no PSI language is
 * registered for Gherkin in this plugin — see `ReqnrollCommentTogglePromoter`'s doc comment for
 * the same fact), unlike `.cs` files where Rider's native rename already owns it (see
 * [RenameCSharpStepAction]). The actual request/disambiguation/apply logic lives in
 * [RenameStepRunner].
 */
class RenameFeatureStepAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible =
            e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
                file != null && file.extension.equals("feature", ignoreCase = true)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return

        val uri = localPathToLspUri(file.path)
        val position = editor.caretModel.logicalPosition

        RenameStepRunner.run(project, uri, position.line, position.column)
    }
}
