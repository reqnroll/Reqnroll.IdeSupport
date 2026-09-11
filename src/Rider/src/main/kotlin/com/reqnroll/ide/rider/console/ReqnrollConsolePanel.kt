package com.reqnroll.ide.rider.console

import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.ui.ConsoleView
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.wm.ToolWindow
import com.reqnroll.ide.rider.logging.ReqnrollConsoleSink
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import java.time.Instant
import javax.swing.JComponent

/**
 * The "Reqnroll" tool window's content (issue #662): a plain [ConsoleView] that
 * [ReqnrollDebugLogger] tees curated (`curated = true`) entries into, alongside its existing file
 * write — the Rider equivalent of the VS extension's `VsOutputPaneLogger` (#651/#656) and the VS
 * Code extension's "Reqnroll" output channel (#661).
 *
 * Deliberately just a [ConsoleView], not the full [ReqnrollDebugLogger] firehose: per-request
 * diagnostic call sites (folding, inlay hints, breadcrumbs, per-viewport CodeLens/documentSymbol,
 * etc.) still log at `curated = false` and stay file-only — see the call sites `curated = true`
 * was added to for what counts as "lifecycle + command outcome" here, matching the bar #658
 * applies to the VS extension.
 */
class ReqnrollConsolePanel(project: Project, private val toolWindow: ToolWindow) : Disposable, ReqnrollConsoleSink {
    private val console: ConsoleView = TextConsoleBuilderFactory.getInstance().createBuilder(project).console

    val component: JComponent get() = console.component

    init {
        Disposer.register(this, console)
        ReqnrollDebugLogger.addConsoleSink(this)
    }

    override fun dispose() {
        ReqnrollDebugLogger.removeConsoleSink(this)
    }

    /** [ReqnrollDebugLogger] callback — may arrive on any thread, so printing is always dispatched to the EDT. */
    override fun accept(level: String, message: String, throwable: Throwable?) {
        ApplicationManager.getApplication().invokeLater {
            if (Disposer.isDisposed(toolWindow.disposable)) return@invokeLater

            val contentType = contentTypeForLevel(level)
            console.print("${ReqnrollDebugLogger.formatLine(Instant.now(), level, message)}\n", contentType)
            if (throwable != null) {
                console.print(
                    throwable.stackTraceToString().trimEnd().prependIndent("    ") + "\n",
                    ConsoleViewContentType.ERROR_OUTPUT,
                )
            }

            // Warning-or-worse auto-activates the tool window, matching VsOutputPaneLogger's
            // ShouldActivate on the VS side and VS Code's autoShowOnWarnOrError (#661) — a plain
            // Info entry (e.g. "LSP client connected") is written but doesn't steal focus.
            if (level != "Info") {
                toolWindow.activate(null)
            }
        }
    }
}

/**
 * Maps a [ReqnrollDebugLogger] level string onto the [ConsoleView] content type it renders as.
 * A top-level function (not a [ReqnrollConsolePanel] member) purely so it's unit-testable without
 * constructing the panel itself, which requires a live `Project`/`ToolWindow`.
 */
internal fun contentTypeForLevel(level: String): ConsoleViewContentType = when (level) {
    "Error" -> ConsoleViewContentType.LOG_ERROR_OUTPUT
    "Warning" -> ConsoleViewContentType.LOG_WARNING_OUTPUT
    else -> ConsoleViewContentType.NORMAL_OUTPUT
}
