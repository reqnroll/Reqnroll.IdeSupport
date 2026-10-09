package com.reqnroll.ide.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.reqnroll.ide.rider.lsp.localPathToLspUri

/**
 * Go to Hooks — the Rider-side surface for the position-based `reqnroll/findHooks` request
 * (issue #158). Mirrors VS's GoToHooksCommand and VS Code's `doGoToHooks`: only enabled with the
 * caret in a `.feature` file editor, sends the current caret position, and navigates directly to
 * the single applicable hook or shows a chooser popup when there are several. The actual
 * request/navigation logic lives in [GoToHooksRunner].
 */
class GoToHooksAction : AnAction() {
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
        val position = lspCaretPosition(editor)

        GoToHooksRunner.runAndShow(
            project, uri, position.line, position.character,
            source = GoToHooksRunner.sourceForPlace(e.place),
        )
    }
}
