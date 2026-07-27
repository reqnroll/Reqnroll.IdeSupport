package com.reqnroll.ide.rider.actions

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.FindStepUsageItem
import com.reqnroll.ide.rider.lsp.protocol.FindStepUsagesResponse

/**
 * Shared "run `reqnroll/findStepUsages` then show the result" logic, used by both
 * [FindStepUsagesAction] (command palette / editor context menu) and the step-usages CodeVision
 * lens's click handler — both need the exact same background-request + popup/message behavior.
 */
object FindStepUsagesRunner {
    /** Runs the request on a background task and shows the result once it completes. */
    fun runAndShow(project: Project, uri: String, line: Int, character: Int) {
        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Finding Step Usages", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.findStepUsages(project, uri, line, character)
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    showResult(project, response)
                }
            }
        })
    }

    /** Shows the "this step definition has no usages" message directly, without another request (used when a CodeLens already reports a 0-usage count). */
    fun showNoUsages(project: Project) {
        Messages.showInfoMessage(
            project, "This step definition has no usages in any feature file.", "Find Step Usages")
    }

    private fun showResult(project: Project, response: FindStepUsagesResponse?) {
        if (response == null) {
            Messages.showErrorDialog(
                project, "The Reqnroll LSP server is not running or did not respond.", "Find Step Usages")
            return
        }

        if (!response.isBinding) {
            Messages.showInfoMessage(
                project, "The caret is not on a step definition binding.", "Find Step Usages")
            return
        }

        if (response.locations.isEmpty()) {
            showNoUsages(project)
            return
        }

        ReqnrollResultPopup.show(
            project,
            "${response.locations.size} Step Usage(s)",
            response.locations,
            render = { item -> renderLabel(item) },
            onChosen = { item -> ReqnrollResultPopup.navigateToUri(project, item.uri, item.startLine, item.startChar) },
        )
    }

    /** `internal` (rather than private) purely so it's unit-testable without an AnAction/platform fixture. */
    internal fun renderLabel(item: FindStepUsageItem): String {
        val keyword = item.keyword?.let { "$it " } ?: ""
        val text = item.stepText ?: item.uri.substringAfterLast('/')
        val featureAndRule = listOfNotNull(item.featureName, item.ruleName).joinToString(" / ")
        val feature = featureAndRule.takeIf { it.isNotEmpty() }?.let { " ($it)" } ?: ""
        val scenario = item.scenarioName?.let { " — $it" } ?: ""
        val project = item.projectName?.let { " [$it]" } ?: ""
        return "$keyword$text$feature$scenario$project"
    }
}
