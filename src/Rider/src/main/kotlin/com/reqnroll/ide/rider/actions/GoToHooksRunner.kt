package com.reqnroll.ide.rider.actions

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.FindHookLocation
import com.reqnroll.ide.rider.lsp.protocol.FindHooksResponse
import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter

/**
 * Shared "run `reqnroll/findHooks` then navigate" logic for [GoToHooksAction] — the Rider-side
 * surface for Hook Navigation. Mirrors VS Code's `doGoToHooks` (src/VSCode/src/commands/goToHooks.ts):
 * a single applicable hook navigates directly, multiple hooks show a chooser popup — unless
 * [runAndShow]'s [alwaysShowPicker] is set, in which case even a single hook shows the popup.
 */
object GoToHooksRunner {
    /**
     * Runs the request on a background task and navigates (or shows a chooser) once it
     * completes. [ownLevelOnly] is set only when invoked from a hook-count CodeVision lens
     * (HookCodeVisionProvider/StepHooksCodeVisionProvider), so the picker matches exactly what
     * the lens counted. [alwaysShowPicker] is likewise set only by those lenses (issue #372
     * follow-up — mirrors VS Code's `alwaysShowPicker`), so clicking a lens always lets the user
     * see which hook it refers to rather than jumping straight there; the manual "Go to Hooks"
     * action (GoToHooksAction) keeps the direct-navigate shortcut for a single match.
     */
    fun runAndShow(
        project: Project, uri: String, line: Int, character: Int,
        ownLevelOnly: Boolean = false, alwaysShowPicker: Boolean = false,
    ) {
        ReqnrollDebugLogger.info("GoToHooksRunner: invoked for $uri at $line:$character")

        // A genuine navigation -- GoToHooksAction is this function's only caller, and Rider's own
        // hook-count CodeVision lens resolves its counts without ever calling reqnroll/findHooks,
        // so unlike VS's classic CodeLens there's no prefetch call through here to mislabel
        // (issue #698).
        RiderTelemetryTransmitter.transmit(RiderTelemetryTransmitter.GO_TO_HOOK_COMMAND_EXECUTED, emptyMap())

        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Finding Hooks", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.findHooks(project, uri, line, character, ownLevelOnly)
                ReqnrollDebugLogger.info(
                    "GoToHooksRunner: ${response?.hooks?.size ?: "null"} hook(s) returned")
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    showResult(project, response, alwaysShowPicker)
                }
            }
        })
    }

    private fun showResult(project: Project, response: FindHooksResponse?, alwaysShowPicker: Boolean) {
        if (response == null) {
            ReqnrollNotify.error(
                project, "The Reqnroll LSP server is not running or did not respond.", "Go to Hooks")
            return
        }

        if (response.hooks.isEmpty()) {
            ReqnrollNotify.info(project, "No hooks found at this position.", "Go to Hooks")
            return
        }

        if (shouldNavigateDirectly(response.hooks.size, alwaysShowPicker)) {
            navigate(project, response.hooks[0])
            return
        }

        ReqnrollResultPopup.show(
            project,
            "${response.hooks.size} Hook(s)",
            response.hooks,
            render = { item -> renderLabel(item) },
            onChosen = { item -> navigate(project, item) },
        )
    }

    private fun navigate(project: Project, item: FindHookLocation) =
        ReqnrollResultPopup.navigateToUri(project, item.uri, item.startLine, item.startChar)

    /** `internal` (rather than private) purely so it's unit-testable without an AnAction/platform fixture. */
    internal fun renderLabel(item: FindHookLocation): String {
        val fileName = item.uri.substringAfterLast('/')
        return "[${item.hookType}] ${item.methodName} ($fileName:${item.startLine + 1})"
    }

    /**
     * True when a single hook should navigate directly rather than showing the picker —
     * i.e. exactly one hook and [alwaysShowPicker] not set. `internal` purely so it's
     * unit-testable without a platform fixture.
     */
    internal fun shouldNavigateDirectly(hookCount: Int, alwaysShowPicker: Boolean): Boolean =
        hookCount == 1 && !alwaysShowPicker
}
