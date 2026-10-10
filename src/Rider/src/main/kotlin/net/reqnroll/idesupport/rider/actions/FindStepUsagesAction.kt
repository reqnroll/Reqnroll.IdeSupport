package net.reqnroll.idesupport.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri

/**
 * Find Step Definition Usages — the Rider-side surface for the position-based
 * `reqnroll/findStepUsages` request. Mirrors VS Code's `doFindStepUsages`
 * (src/VSCode/src/stepUsages.ts): only enabled with the caret in a `.cs` file (matching VS
 * Code's `editorLangId == csharp` context-menu condition), sends the current caret position,
 * and shows a chooser popup of matching feature-file steps, navigating on selection. The
 * actual request/display logic lives in [FindStepUsagesRunner], shared with the step-usages
 * CodeVision lens's click handler.
 */
class FindStepUsagesAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible =
            e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
                file != null && file.extension.equals("cs", ignoreCase = true)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return

        val uri = localPathToLspUri(file.path)
        val position = editor.caretModel.logicalPosition

        FindStepUsagesRunner.runAndShow(project, uri, position.line, position.column)
    }
}
