package net.reqnroll.idesupport.rider.navigation

import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.ex.ActionUtil
import com.intellij.openapi.editor.Editor
import net.reqnroll.idesupport.rider.codevision.NativeLspCodeLens
import net.reqnroll.idesupport.rider.isFeatureExtension
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.taglinks.ReqnrollFeatureTagLinkController
import java.awt.event.InputEvent
import java.awt.event.MouseEvent

/**
 * Makes F12 / Ctrl+B / Ctrl+click Go to Declaration reach the LSP server for `.feature` files on
 * Rider 2026.2+.
 *
 * There the keyboard and mouse shortcuts for `GotoDeclaration` are routed through Rider's
 * `BackendDelegatingAction`, which asks the ReSharper backend first; the backend knows nothing
 * about Gherkin and answers "Cannot locate a declaration to go to" without the platform's LSP
 * definition support ever running (the server log shows no `textDocument/definition`). Choosing
 * Go To > Declaration or Usages from the menu works, because that path ends in the wrapped frontend
 * action. This action is bound to the same shortcuts (`use-shortcut-of`), wins them for `.feature`
 * files via [ReqnrollGoToDefinitionPromoter], and runs that wrapped frontend action directly.
 *
 * On a tag link (`Traceability.TagLinks`) it opens the link instead, through
 * [ReqnrollFeatureTagLinkController.openTagLinkAt].
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
                handlesFeatureShortcuts
    }

    override fun actionPerformed(e: AnActionEvent) {
        val editor = e.getData(CommonDataKeys.EDITOR)
        if (editor != null && ReqnrollFeatureTagLinkController.openTagLinkAt(editor, targetOffset(editor, e.inputEvent))) {
            // A tag link: Go to Declaration would follow it only once the platform had fetched the document links.
            return
        }
        val registered = ActionManager.getInstance().getAction(GO_TO_DECLARATION_ID) ?: return
        val target = frontendActionOf(registered)
        ReqnrollDebugLogger.info(
            "ReqnrollGoToDefinitionAction: running ${target.javaClass.name} (registered as ${registered.javaClass.name})")
        GoToStepDefinitionTelemetry.arm()
        ActionUtil.invokeAction(target, e.dataContext, e.place, e.inputEvent, null)
    }

    companion object {
        internal const val GO_TO_DECLARATION_ID = "GotoDeclaration"

        /** Whether this action takes Go to Declaration's shortcuts (and Ctrl/Cmd+click) for `.feature` files. */
        val handlesFeatureShortcuts: Boolean get() = NativeLspCodeLens.isRenderedByPlatform

        /** The offset under the mouse for a click on [editor], otherwise the caret offset. */
        private fun targetOffset(editor: Editor, inputEvent: InputEvent?): Int =
            if (inputEvent is MouseEvent && inputEvent.component === editor.contentComponent) {
                editor.logicalPositionToOffset(editor.xyToLogicalPosition(inputEvent.point))
            } else {
                editor.caretModel.offset
            }

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
