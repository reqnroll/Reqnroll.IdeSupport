package com.reqnroll.ide.rider.actions

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.FindUnusedStepDefinitionsResponse
import com.reqnroll.ide.rider.lsp.protocol.UnusedStepDefinitionItem

/**
 * Find Unused Step Definitions (F15) — the Rider-side surface for the workspace-wide
 * `reqnroll/findUnusedStepDefinitions` request. Mirrors VS Code's `doFindUnusedStepDefinitions`
 * (src/VSCode/src/stepUsages.ts): runs the scan with a progress indicator, then shows a chooser
 * popup of results, navigating to the picked binding's source on selection.
 */
class FindUnusedStepDefinitionsAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return

        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Scanning for Unused Step Definitions", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.findUnusedStepDefinitions(project)
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    showResult(project, response)
                }
            }
        })
    }

    private fun showResult(project: Project, response: FindUnusedStepDefinitionsResponse?) {
        if (response == null) {
            Messages.showErrorDialog(
                project, "The Reqnroll LSP server is not running or did not respond.",
                "Find Unused Step Definitions")
            return
        }

        if (response.items.isEmpty()) {
            Messages.showInfoMessage(project, "No unused step definitions found.", "Find Unused Step Definitions")
            return
        }

        ReqnrollResultPopup.show(
            project,
            "${response.items.size} Unused Step Definition(s)",
            response.items,
            render = { item -> renderLabel(item) },
            onChosen = { item -> navigateOrExplain(project, item) },
        )
    }

    /**
     * Navigates to the picked binding, or explains why it cannot be opened. The server sends a null
     * [UnusedStepDefinitionItem.sourceFile] when the binding's source is not on this machine, so
     * navigating would otherwise silently do nothing (issue #540).
     */
    private fun navigateOrExplain(project: Project, item: UnusedStepDefinitionItem) {
        if (item.sourceFile.isNullOrBlank()) {
            val recorded = item.recordedSourceFile
            val where = if (recorded != null) " The compiled assembly records it at \"$recorded\"." else ""
            Messages.showWarningDialog(
                project,
                "This step definition's source isn't on this machine.$where " +
                    "Rebuild the project locally to navigate to it.",
                "Find Unused Step Definitions")
            return
        }

        ReqnrollResultPopup.navigateToPath(project, item.sourceFile, item.sourceLine, item.sourceChar)
    }

    companion object {
        /** Pulled out to `internal` (rather than a private member function) purely so it's unit-testable without an AnAction/platform fixture. */
        internal fun renderLabel(item: UnusedStepDefinitionItem): String {
            val name = listOfNotNull(item.className, item.methodName).joinToString(".")
            val expression = item.bindingExpression?.let { " — $it" } ?: ""
            val project = item.projectName?.let { " [$it]" } ?: ""
            // Marks a row that cannot be navigated to, so the popup doesn't present it as
            // identical to the rest and then do nothing when it's chosen (issue #540).
            val unresolved = if (item.isResolved) "" else " (source not on this machine)"
            return "$name$expression$project$unresolved"
        }
    }
}
