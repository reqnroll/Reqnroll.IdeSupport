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
        val file = CommonDataKeys.VIRTUAL_FILE.getData(context) ?: return emptyList()
        if (!isFeatureExtension(file.extension) || !NativeLspCodeLens.isRenderedByPlatform) return emptyList()

        val actionManager = ActionManager.getInstance()
        return actions.filter { actionManager.getId(it) == ReqnrollGoToDefinitionAction.GO_TO_DECLARATION_ID }
    }
}
