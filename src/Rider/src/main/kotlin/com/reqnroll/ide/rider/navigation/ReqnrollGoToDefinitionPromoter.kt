package com.reqnroll.ide.rider.navigation

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionPromoter
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataContext
import com.reqnroll.ide.rider.codevision.NativeLspCodeLens
import com.reqnroll.ide.rider.isFeatureExtension

/**
 * Suppresses the platform's `GotoDeclaration` from its own shortcuts' candidate list for `.feature`
 * files (Rider 2026.2+ only), so [ReqnrollGoToDefinitionAction] — bound to the same shortcuts —
 * fires instead. Same technique as [com.reqnroll.ide.rider.commenting.ReqnrollCommentTogglePromoter].
 */
class ReqnrollGoToDefinitionPromoter : ActionPromoter {
    override fun suppress(actions: List<AnAction>, context: DataContext): List<AnAction> {
        if (!appliesTo(context)) return emptyList()

        val actionManager = ActionManager.getInstance()
        return actions.filter { actionManager.getId(it) == ReqnrollGoToDefinitionAction.GO_TO_DECLARATION_ID }
    }

    /**
     * Also puts ours first: F12 and Ctrl+click resolve to several actions sharing the shortcut
     * (`GotoDeclaration`, `ClickLink`, Rider's `InlayClickAction`, ...), and suppressing only
     * `GotoDeclaration` left another of them to win the shortcut ahead of this action (#909).
     */
    override fun promote(actions: List<AnAction>, context: DataContext): List<AnAction> {
        if (!appliesTo(context)) return emptyList()
        return actions.filterIsInstance<ReqnrollGoToDefinitionAction>()
    }

    private fun appliesTo(context: DataContext): Boolean {
        val file = CommonDataKeys.VIRTUAL_FILE.getData(context) ?: return false
        return isFeatureExtension(file.extension) && NativeLspCodeLens.isRenderedByPlatform
    }
}
