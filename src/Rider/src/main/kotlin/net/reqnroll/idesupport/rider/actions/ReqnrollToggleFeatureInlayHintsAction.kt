package net.reqnroll.idesupport.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import net.reqnroll.idesupport.rider.inlayhints.NativeLspInlayHints
import net.reqnroll.idesupport.rider.inlayhints.ReqnrollFeatureInlayHintsController
import net.reqnroll.idesupport.rider.inlayhints.ReqnrollFeatureInlayHintsSettings

/**
 * The only on/off switch for [ReqnrollFeatureInlayHintsController]'s hints (see
 * [ReqnrollFeatureInlayHintsSettings]'s doc comment for why this can't instead live in the
 * platform's own Settings > Editor > Inlay Hints page). A checkable Tools-menu item rather than a
 * dedicated settings page — the simplest surface for a single boolean.
 */
class ReqnrollToggleFeatureInlayHintsAction : ToggleAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    // On Rider 2026.2+ the platform renders the hints (see NativeLspInlayHints), so this switch controls nothing there.
    override fun update(e: AnActionEvent) {
        super.update(e)
        if (NativeLspInlayHints.isRenderedByPlatform) e.presentation.isEnabledAndVisible = false
    }

    override fun isSelected(e: AnActionEvent): Boolean = ReqnrollFeatureInlayHintsSettings.isEnabled

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        ReqnrollFeatureInlayHintsSettings.isEnabled = state
        e.project?.let { ReqnrollFeatureInlayHintsController.refreshOpenFeatureEditors(it) }
    }
}
