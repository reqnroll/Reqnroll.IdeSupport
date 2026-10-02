package com.reqnroll.ide.rider.console

import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.content.ContentFactory

/**
 * Registers the "Reqnroll" tool window (issue #662) — a curated, app-level-only console: plugin
 * lifecycle (LSP server launching, connection failures) and the outcome of user-invoked actions
 * (Find Step Usages, Find Unused Step Definitions, Go To Hooks, Rename Step, Run, etc.), not the
 * raw per-server LSP wire traffic the platform's own "Language Servers" tool window already shows
 * for every running server. See [ReqnrollConsolePanel]'s doc comment for what does/doesn't reach
 * it. Mirrors [com.reqnroll.ide.rider.structureview.ReqnrollStructureToolWindowFactory]'s
 * dedicated-`ToolWindowFactory` shape (issue #163 prior art).
 */
class ReqnrollConsoleToolWindowFactory : ToolWindowFactory {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = ReqnrollConsolePanel(project, toolWindow)
        Disposer.register(toolWindow.disposable, panel)

        val content = ContentFactory.getInstance().createContent(panel.component, null, false)
        toolWindow.contentManager.addContent(content)
    }
}
