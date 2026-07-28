package com.reqnroll.ide.rider.actions

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.GoToMatchingScenariosResponse
import com.reqnroll.ide.rider.lsp.protocol.MatchingScenarioLocation

/**
 * Shared "run `reqnroll/goToMatchingScenarios` then navigate" logic for the hook-match-count
 * CodeVision lens's click action (issue #373) — the inverse of [GoToHooksRunner]. Only ever
 * invoked from a CodeLens click with the lens's own attribute location, so unlike
 * [GoToHooksRunner] (also reachable from a dedicated caret-position action) there's no separate
 * `AnAction`/menu entry here.
 */
object GoToMatchingScenariosRunner {
    /** Runs the request on a background task and navigates (or shows a chooser) once it completes. */
    fun runAndShow(project: Project, uri: String, line: Int, character: Int) {
        ReqnrollDebugLogger.info("GoToMatchingScenariosRunner: invoked for $uri at $line:$character")
        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Finding Matching Scenarios", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.goToMatchingScenarios(project, uri, line, character)
                ReqnrollDebugLogger.info(
                    "GoToMatchingScenariosRunner: ${response?.scenarios?.size ?: "null"} scenario(s) returned")
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    showResult(project, response)
                }
            }
        })
    }

    private fun showResult(project: Project, response: GoToMatchingScenariosResponse?) {
        if (response == null) {
            Messages.showErrorDialog(
                project, "The Reqnroll LSP server is not running or did not respond.", "Go to Matching Scenarios")
            return
        }

        if (response.scenarios.isEmpty()) {
            Messages.showInfoMessage(project, "This hook has no matching scenarios.", "Go to Matching Scenarios")
            return
        }

        if (response.scenarios.size == 1) {
            navigate(project, response.scenarios[0])
            return
        }

        ReqnrollResultPopup.show(
            project,
            "${response.scenarios.size} Matching Scenario(s)",
            response.scenarios,
            render = { item -> renderLabel(item) },
            onChosen = { item -> navigate(project, item) },
        )
    }

    private fun navigate(project: Project, item: MatchingScenarioLocation) =
        ReqnrollResultPopup.navigateToUri(project, item.uri, item.startLine, item.startChar)

    /** `internal` (rather than private) purely so it's unit-testable without an AnAction/platform fixture. */
    internal fun renderLabel(item: MatchingScenarioLocation): String {
        val name = item.scenarioName.ifBlank { "(untitled)" }
        val kind = if (item.isOutline) "Scenario Outline" else "Scenario"
        return "[$kind] $name"
    }
}
