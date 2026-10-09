package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerEvent
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.vfs.VirtualFile
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.protocol.DocumentActivatedParams
import com.reqnroll.ide.rider.lsp.localPathToLspUri

/**
 * Feeds `reqnroll/documentActivated` (issue #85) — Phase 4 of
 * docs/Rider-Project-Document-Sync-Implementation-Plan.md. Ported from VS's
 * `DocumentActivationTrackingInterceptor` + `DocumentActivationState`, adapted to the events
 * Rider's platform actually gives us: VS observes `textDocument/didOpen`/`didClose` directly by
 * intercepting the raw LSP pipe (no such hook exists on Rider — established earlier in this
 * plugin's development), so this uses `FileEditorManagerListener`'s `fileOpened`/`fileClosed`/
 * `selectionChanged` as the equivalent editor-level signal instead — the platform's own generic
 * LSP client sends the real `didOpen`/`didClose` in lockstep with these anyway.
 */
class ReqnrollDocumentActivationSync : ProjectActivity {
    override suspend fun execute(project: Project) {
        val dispatcher = DocumentActivationDispatcher(
            DocumentActivationState(),
            whenRunning = { action -> ReqnrollLspServerReadiness.runWhenRunning(project, action) },
            send = { path -> send(project, path) },
        )

        project.messageBus.connect(project).subscribe(
            FileEditorManagerListener.FILE_EDITOR_MANAGER,
            object : FileEditorManagerListener {
                override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
                    if (!isFeatureFile(file)) return
                    dispatcher.onFileOpened(file.path)
                }

                override fun fileClosed(source: FileEditorManager, file: VirtualFile) {
                    if (!isFeatureFile(file)) return
                    dispatcher.onFileClosed(file.path)
                }

                override fun selectionChanged(event: FileEditorManagerEvent) {
                    val file = event.newFile ?: return
                    if (!isFeatureFile(file)) return
                    dispatcher.onFileSelected(file.path)
                }
            },
        )
    }

    private fun isFeatureFile(file: VirtualFile) = file.extension.equals("feature", ignoreCase = true)

    private fun send(project: Project, path: String) {
        // Same URI form as the platform's own textDocument/didOpen for this file — see localPathToLspUri.
        val uri = localPathToLspUri(path)
        ReqnrollDebugLogger.verbose("documentActivated: $uri")
        ReqnrollNotificationSender.sendDocumentActivated(project, DocumentActivatedParams(uri))
    }
}

/**
 * Turns [DocumentActivationState] decisions into notifications (issue #986). The notification
 * must not reach the server before it is Running (an Initializing server rejects it as an
 * "Unexpected notification"), which is exactly the situation for restored tabs at project open,
 * so every SEND_NOW is routed through [whenRunning] (`ReqnrollLspServerReadiness.runWhenRunning`).
 * The state machine marks the file ACTIVATED at decision time; because [whenRunning] defers
 * rather than drops, that stays correct — the send is owed and is delivered once the server is
 * Running. If the file is closed (or re-opened) while the send is still deferred, the deferred
 * send is skipped: that open-lifetime has ended and the next one gets its own activation.
 */
internal class DocumentActivationDispatcher(
    private val state: DocumentActivationState,
    private val whenRunning: (() -> Unit) -> Unit,
    private val send: (String) -> Unit,
) {
    fun onFileOpened(path: String) = dispatch(path, state.onDidOpen(path))

    fun onFileSelected(path: String) = dispatch(path, state.onWindowActivated(path))

    fun onFileClosed(path: String) = state.onDidClose(path)

    private fun dispatch(path: String, action: DocumentActivationAction) {
        if (action != DocumentActivationAction.SEND_NOW) return
        val generation = state.generation(path)
        whenRunning {
            if (state.generation(path) == generation && state.isActivated(path)) send(path)
        }
    }
}

/** Ported verbatim from DocumentActivationState.cs's four-phase design — see that file's remarks. */
internal enum class DocumentActivationPhase { NOT_SEEN, OPENED, ACTIVATION_PENDING, ACTIVATED }

internal enum class DocumentActivationAction { NONE, SEND_NOW }

/** Thread-safe per-file state machine tracking whether a document-activation notification is still owed. */
internal class DocumentActivationState {
    private val lock = Any()
    private val phases = HashMap<String, DocumentActivationPhase>()
    private val generations = HashMap<String, Int>()

    fun onWindowActivated(filePath: String): DocumentActivationAction = synchronized(lock) {
        when (getPhase(filePath)) {
            DocumentActivationPhase.NOT_SEEN -> {
                phases[filePath] = DocumentActivationPhase.ACTIVATION_PENDING
                DocumentActivationAction.NONE
            }
            DocumentActivationPhase.OPENED -> {
                phases[filePath] = DocumentActivationPhase.ACTIVATED
                DocumentActivationAction.SEND_NOW
            }
            DocumentActivationPhase.ACTIVATION_PENDING, DocumentActivationPhase.ACTIVATED ->
                DocumentActivationAction.NONE
        }
    }

    fun onDidOpen(filePath: String): DocumentActivationAction = synchronized(lock) {
        bumpGeneration(filePath)
        when (getPhase(filePath)) {
            DocumentActivationPhase.ACTIVATION_PENDING -> {
                phases[filePath] = DocumentActivationPhase.ACTIVATED
                DocumentActivationAction.SEND_NOW
            }
            // Opened/Activated here means didOpen fired again without an intervening didClose —
            // reset to Opened so the file gets one more activation notification rather than
            // silently staying in a phase that can no longer produce one.
            DocumentActivationPhase.NOT_SEEN, DocumentActivationPhase.OPENED, DocumentActivationPhase.ACTIVATED -> {
                phases[filePath] = DocumentActivationPhase.OPENED
                DocumentActivationAction.NONE
            }
        }
    }

    fun onDidClose(filePath: String) = synchronized(lock) {
        bumpGeneration(filePath)
        phases.remove(filePath)
        Unit
    }

    fun isActivated(filePath: String): Boolean = synchronized(lock) {
        getPhase(filePath) == DocumentActivationPhase.ACTIVATED
    }

    /** Bumped whenever the file's phase is reset (open/close), so a deferred send can detect a new open-lifetime. */
    fun generation(filePath: String): Int = synchronized(lock) { generations[filePath] ?: 0 }

    private fun bumpGeneration(filePath: String) {
        generations[filePath] = (generations[filePath] ?: 0) + 1
    }

    private fun getPhase(filePath: String) = phases[filePath] ?: DocumentActivationPhase.NOT_SEEN
}
