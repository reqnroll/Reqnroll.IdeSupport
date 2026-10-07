package com.reqnroll.ide.rider.navigation

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.ex.ActionUtil
import com.reqnroll.ide.rider.codevision.NativeLspCodeLens
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger

/**
 * Makes F12 / Ctrl+B / Ctrl+click Go to Declaration reach the LSP server for `.feature` files on
 * Rider 2026.2+ (#909).
 *
 * There the keyboard and mouse shortcuts for `GotoDeclaration` are routed through Rider's
 * `BackendDelegatingAction`, which asks the ReSharper backend first; the backend knows nothing
 * about Gherkin and answers "Cannot locate a declaration to go to" without the platform's LSP
 * definition support ever running (the server log shows no `textDocument/definition`). Choosing
 * Go To > Declaration or Usages from the menu works, because that path ends in the wrapped frontend
 * action. This action is bound to the same shortcuts (`use-shortcut-of`), wins them for `.feature`
 * files via [ReqnrollGoToDefinitionPromoter], and runs that wrapped frontend action directly.
 *
 * Only enabled where the problem exists — [NativeLspCodeLens.isRenderedByPlatform] is the
 * "2026.2+ LSP client" signal — so older Riders keep their own, working, behaviour.
 */
class ReqnrollGoToDefinitionAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible =
            e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
                file != null && isFeatureExtension(file.extension) &&
                NativeLspCodeLens.isRenderedByPlatform
    }

    override fun actionPerformed(e: AnActionEvent) {
        val registered = ActionManager.getInstance().getAction(GO_TO_DECLARATION_ID) ?: return
        val target = frontendActionOf(registered)
        ReqnrollDebugLogger.info(
            "ReqnrollGoToDefinitionAction: running ${target.javaClass.name} (registered as ${registered.javaClass.name})")
        ActionUtil.invokeAction(target, e.dataContext, e.place, e.inputEvent, null)
    }

    companion object {
        internal const val GO_TO_DECLARATION_ID = "GotoDeclaration"

        /**
         * The action Rider's `BackendDelegatingAction` wraps. Reflective: that class lives in
         * Rider's remote-development client module, which this plugin doesn't compile against. If
         * [registered] isn't such a wrapper (or the accessor is gone), it is returned unchanged.
         */
        internal fun frontendActionOf(registered: AnAction): AnAction =
            try {
                registered.javaClass.getMethod("getFrontendAction").invoke(registered) as? AnAction ?: registered
            } catch (e: Exception) {
                registered
            }
    }
}
