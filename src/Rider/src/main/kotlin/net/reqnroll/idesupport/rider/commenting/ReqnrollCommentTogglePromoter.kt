package net.reqnroll.idesupport.rider.commenting

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionPromoter
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataContext
import com.intellij.openapi.actionSystem.IdeActions

/**
 * Suppresses the built-in `CommentByLineComment` action (`Ctrl+/`) from its own keystroke's
 * candidate list when the active file is `.feature`, so [ReqnrollToggleCommentAction] — bound to
 * the same default keystroke — fires instead. See [ReqnrollToggleCommentAction]'s doc comment for
 * why decorating the built-in action's `EditorActionHandler` (the first approach tried) doesn't
 * work: that action never consults `EditorActionManager` at all.
 */
class ReqnrollCommentTogglePromoter : ActionPromoter {
    override fun suppress(actions: List<AnAction>, context: DataContext): List<AnAction> {
        val file = CommonDataKeys.VIRTUAL_FILE.getData(context) ?: return emptyList()
        if (!file.extension.equals("feature", ignoreCase = true)) return emptyList()

        val actionManager = ActionManager.getInstance()
        return actions.filter { actionManager.getId(it) == IdeActions.ACTION_COMMENT_LINE }
    }
}
