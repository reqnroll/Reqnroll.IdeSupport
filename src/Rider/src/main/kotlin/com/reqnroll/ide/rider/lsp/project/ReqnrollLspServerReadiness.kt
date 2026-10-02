package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.Disposable
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerManagerListener
import com.intellij.platform.lsp.api.LspServerState
import com.reqnroll.ide.rider.lsp.ReqnrollLspServerSupportProvider

/**
 * Runs [action] once the Reqnroll LSP server reaches [LspServerState.Running] — immediately if
 * it's already there, otherwise deferred via [LspServerManagerListener].
 *
 * Sending any custom `reqnroll`-prefixed notification before the server completes its LSP
 * initialize/initialized handshake gets it dropped and logged as an "Unexpected notification" by
 * OmniSharp's `LspServerReceiver` — confirmed live. The server itself may not even have been
 * *started* yet: it's launched lazily from `ReqnrollLspServerSupportProvider.fileOpened`, but
 * Rider's `runnableProjectsModel.projects` reactive property (subscribed to by
 * [ReqnrollRunnableProjectsListener] and [ReqnrollProjectFilesSync]) fires on its own schedule at
 * project open, independent of any file being opened — so its very first `advise` callback almost
 * always races ahead of server startup. Route every such push through this gate rather than
 * sending directly from an `advise`/model-change callback.
 *
 * Registers the deferred listener against a fresh, per-call [Disposable] (a child of [project],
 * not [project] itself) and disposes it immediately once [action] runs — confirmed by decompiling
 * `LspServerManager` that there is no `removeLspServerManagerListener` at all; the *only* way to
 * deregister is via the parent `Disposable` passed at registration. Without this, a listener
 * registered here would stay subscribed for the rest of the project's lifetime even after firing
 * once, and would fire `action` again on every later "Running" transition — including after a
 * manual LSP server restart (a real, user-reachable action via the status-bar widget), silently
 * turning "run once when ready" into "run every time the server (re)starts."
 */
object ReqnrollLspServerReadiness {
    fun runWhenRunning(project: Project, action: () -> Unit) {
        val manager = LspServerManager.getInstance(project)
        val server = manager.getServersForProvider(ReqnrollLspServerSupportProvider::class.java).firstOrNull()

        if (server?.state == LspServerState.Running) {
            action()
            return
        }

        val listenerLifetime = Disposer.newDisposable(project, "ReqnrollLspServerReadiness.runWhenRunning")
        manager.addLspServerManagerListener(
            object : LspServerManagerListener {
                override fun serverStateChanged(lspServer: LspServer) {
                    if (lspServer.providerClass == ReqnrollLspServerSupportProvider::class.java &&
                        lspServer.state == LspServerState.Running
                    ) {
                        Disposer.dispose(listenerLifetime)
                        action()
                    }
                }
            },
            listenerLifetime,
            false,
        )
    }
}
