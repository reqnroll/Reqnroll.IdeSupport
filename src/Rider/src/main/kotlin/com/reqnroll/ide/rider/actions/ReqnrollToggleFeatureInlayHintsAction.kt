package com.reqnroll.ide.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import com.reqnroll.ide.rider.inlayhints.ReqnrollFeatureInlayHintsController
import com.reqnroll.ide.rider.inlayhints.ReqnrollFeatureInlayHintsSettings

/**
 * The only on/off switch for [ReqnrollFeatureInlayHintsController]'s hints (see
 * [ReqnrollFeatureInlayHintsSettings]'s doc comment for why this can't instead live in the
 * platform's own Settings > Editor > Inlay Hints page). A checkable Tools-menu item rather than a
 * dedicated settings page — the simplest surface for a single boolean.
 */
class ReqnrollToggleFeatureInlayHintsAction : ToggleAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun isSelected(e: AnActionEvent): Boolean = ReqnrollFeatureInlayHintsSettings.isEnabled

    override fun setSelected(e: AnActionEvent, state: Boolean) {
        ReqnrollFeatureInlayHintsSettings.isEnabled = state
        e.project?.let { ReqnrollFeatureInlayHintsController.refreshOpenFeatureEditors(it) }
    }
}
