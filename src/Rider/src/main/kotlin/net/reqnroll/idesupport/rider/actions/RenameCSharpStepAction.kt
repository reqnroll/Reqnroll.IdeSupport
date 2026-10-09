package net.reqnroll.idesupport.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri

/**
 * Rename Step, `.cs`-file side (issue #160) — the Rider-side surface for the
 * `reqnroll/renameTargets` disambiguation + standard `textDocument/rename` flow, invoked from the
 * step-definition attribute. Deliberately context-menu-only, no keyboard shortcut: Rider's native
 * Shift+F6 rename already owns that shortcut for ordinary C# symbols, and this action's own
 * `update()` gate (file extension only, matching [FindStepUsagesAction]'s `.cs` gate) can't tell
 * "caret is on a step attribute" from "caret is on any other C# identifier" cheaply enough to
 * safely claim the shortcut without risking clobbering everyday symbol renames. The actual
 * request/disambiguation/apply logic lives in [RenameStepRunner].
 */
class RenameCSharpStepAction : AnAction() {
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

        RenameStepRunner.run(project, uri, position.line, position.column)
    }
}
