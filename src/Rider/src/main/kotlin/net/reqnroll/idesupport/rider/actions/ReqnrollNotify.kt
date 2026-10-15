package net.reqnroll.idesupport.rider.actions

import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger

/**
 * Wraps [Messages]' modal info/warning/error dialogs — the Rider-side equivalent of VS Code's
 * popup notifications (`vscode.window.show*Message`) — so every command's one-line result is also
 * mirrored into the curated "Reqnroll" console (issue #662), matching VS Code's
 * `logging/appNotify.ts` (issue #661). Signatures deliberately match the [Messages] methods they
 * replace (`project`, `message`, `title`) so call sites are a mechanical swap.
 */
object ReqnrollNotify {
    fun info(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.info("$title: $message")
        Messages.showInfoMessage(project, message, title)
    }

    fun warn(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.warn("$title: $message")
        Messages.showWarningDialog(project, message, title)
    }

    /**
     * Shows the language server's reason for rejecting an edit (rename, comment toggle, …) in a modal
     * dialog and mirrors it to the curated "Reqnroll" console (issue #1132). The single place edit
     * rejections are presented in Rider; must be called on the EDT.
     */
    fun editRejected(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.info("$title: rejected by server: $message")
        Messages.showWarningDialog(project, message, title)
    }

    fun error(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.error("$title: $message")
        Messages.showErrorDialog(project, message, title)
    }
}
