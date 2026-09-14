package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerManager
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.protocol.DocumentActivatedParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollLanguageServer
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectFilesParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectLoadedParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectUnloadedParams
import com.reqnroll.ide.rider.lsp.protocol.RenameAppliedParams
import com.reqnroll.ide.rider.lsp.protocol.SelectRenameTargetParams

/**
 * Sends the reqnroll-prefixed lifecycle notifications to whichever Reqnroll LSP server(s) are running
 * for [Project], via the mechanism confirmed in Phase 0 against Rider 2024.3.5's actual bundled
 * classes (see docs/Rider-Project-Document-Sync-Implementation-Plan.md §3.1):
 * `LspServerManager.getServersForProvider(...)` + `LspServer.sendNotification { ... }`. Mirrors
 * VS's `VsProjectEventMonitor.TrySend*Async` try/catch-and-log pattern — a send failure must
 * never break whatever caller triggered it (a file event, a listener callback, etc.).
 */
object ReqnrollNotificationSender {
    fun sendProjectLoaded(project: Project, params: ReqnrollProjectLoadedParams) =
        send(project, "projectLoaded") { it.projectLoaded(params) }

    fun sendProjectUnloaded(project: Project, params: ReqnrollProjectUnloadedParams) =
        send(project, "projectUnloaded") { it.projectUnloaded(params) }

    fun sendProjectFiles(project: Project, params: ReqnrollProjectFilesParams) =
        send(project, "projectFiles") { it.projectFiles(params) }

    fun sendDocumentActivated(project: Project, params: DocumentActivatedParams) =
        send(project, "documentActivated") { it.documentActivated(params) }

    fun sendSelectRenameTarget(project: Project, params: SelectRenameTargetParams) =
        send(project, "selectRenameTarget") { it.selectRenameTarget(params) }

    fun sendRenameApplied(project: Project, params: RenameAppliedParams) =
        send(project, "renameApplied") { it.renameApplied(params) }

    private fun send(project: Project, methodName: String, invoke: (ReqnrollLanguageServer) -> Unit) {
        val servers = LspServerManager.getInstance(project)
            .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)

        if (servers.isEmpty()) {
            ReqnrollDebugLogger.verbose("$methodName: no Reqnroll LSP server running, notification dropped")
            return
        }

        servers.forEach { server ->
            try {
                server.sendNotification { languageServer -> invoke(languageServer as ReqnrollLanguageServer) }
            } catch (ex: Exception) {
                ReqnrollDebugLogger.verbose("$methodName: failed to send notification", ex)
            }
        }
    }
}
