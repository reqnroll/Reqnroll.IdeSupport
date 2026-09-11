package com.reqnroll.ide.rider.actions

import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger

/**
 * Wraps [Messages]' modal info/warning/error dialogs — the Rider-side equivalent of VS Code's
 * popup notifications (`vscode.window.show*Message`) — so every command's one-line result is also
 * mirrored into the curated "Reqnroll" console (issue #662), matching VS Code's
 * `logging/appNotify.ts` (issue #661). Signatures deliberately match the [Messages] methods they
 * replace (`project`, `message`, `title`) so call sites are a mechanical swap.
 */
object ReqnrollNotify {
    fun info(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.info("$title: $message", curated = true)
        Messages.showInfoMessage(project, message, title)
    }

    fun warn(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.warn("$title: $message", curated = true)
        Messages.showWarningDialog(project, message, title)
    }

    fun error(project: Project, message: String, title: String) {
        ReqnrollDebugLogger.error("$title: $message", curated = true)
        Messages.showErrorDialog(project, message, title)
    }
}
