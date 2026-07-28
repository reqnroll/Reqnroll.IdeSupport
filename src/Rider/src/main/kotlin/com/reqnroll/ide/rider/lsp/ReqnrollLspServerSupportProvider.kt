package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.reqnroll.ide.rider.isCsExtension
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.project.ReqnrollLspServerReadiness
import com.reqnroll.ide.rider.lsp.project.ReqnrollProjectBaseline

/**
 * Entry point Rider's platform calls whenever a file is opened anywhere in the IDE; the only
 * place that decides whether the Reqnroll LSP server should be running at all. Registered via
 * `plugin.xml`'s `platform.lsp.serverSupportProvider` extension.
 */
class ReqnrollLspServerSupportProvider : LspServerSupportProvider {
    /** Starts (or reuses) the Reqnroll server for `.feature`/`.cs` files; ignores everything else. */
    override fun fileOpened(
        project: Project,
        file: VirtualFile,
        serverStarter: LspServerSupportProvider.LspServerStarter,
    ) {
        if (!isFeatureExtension(file.extension) && !isCsExtension(file.extension)) {
            return
        }

        ReqnrollDebugLogger.info("fileOpened: starting/reusing LSP server for ${file.path}")
        serverStarter.ensureServerStarted(ReqnrollLspServerDescriptor(project))

        // Closes a race the project/document-sync ProjectActivity listeners can't close on their
        // own: they run at project open (independent of any file being opened) and their initial
        // advise() fire almost always finds no server running yet, since server startup itself is
        // gated on this very fileOpened callback — that first push is silently dropped, and unlike
        // VS (which has its own preload side channel for the same problem), there's no guarantee
        // anything re-triggers those listeners' advise callback again in the same session. Re-push
        // the current snapshot once the server is genuinely ready (see pushBaselineOnceRunning);
        // safe/idempotent even if the listeners' own push already succeeded (the server treats a
        // repeat baseline for an already-loaded project as an update, not a duplicate).
        pushBaselineOnceRunning(project)
    }

    /**
     * ensureServerStarted only *initiates* startup — it returns well before the LSP
     * initialize/initialized handshake completes, while the server is still in
     * [com.intellij.platform.lsp.api.LspServerState.Initializing]. Pushing the baseline
     * synchronously right after it (as this used to do) sent the notification before the server
     * considered itself ready to receive anything, and the server correctly rejected it as an
     * "Unexpected notification" per the LSP spec — confirmed live: `reqnroll/projectLoaded`/
     * `projectFiles` showed up in OmniSharp's `LspServerReceiver` warning log, and the server's own
     * log never logged `HandleProjectLoadedAsync` at all for that session. Deferred via
     * [ReqnrollLspServerReadiness] rather than sent directly.
     */
    private fun pushBaselineOnceRunning(project: Project) {
        ReqnrollLspServerReadiness.runWhenRunning(project) {
            ReqnrollProjectBaseline.pushForAllRunnableProjects(project)
        }
    }
}
