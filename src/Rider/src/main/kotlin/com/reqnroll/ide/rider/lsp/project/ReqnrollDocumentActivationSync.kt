package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerEvent
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.protocol.DocumentActivatedParams

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
        val state = DocumentActivationState()

        project.messageBus.connect(project).subscribe(
            FileEditorManagerListener.FILE_EDITOR_MANAGER,
            object : FileEditorManagerListener {
                override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
                    if (!isFeatureFile(file)) return
                    if (state.onDidOpen(file.path) == DocumentActivationAction.SEND_NOW) {
                        send(project, file)
                    }
                }

                override fun fileClosed(source: FileEditorManager, file: VirtualFile) {
                    if (!isFeatureFile(file)) return
                    state.onDidClose(file.path)
                }

                override fun selectionChanged(event: FileEditorManagerEvent) {
                    val file = event.newFile ?: return
                    if (!isFeatureFile(file)) return
                    if (state.onWindowActivated(file.path) == DocumentActivationAction.SEND_NOW) {
                        send(project, file)
                    }
                }
            },
        )
    }

    private fun isFeatureFile(file: VirtualFile) = file.extension.equals("feature", ignoreCase = true)

    private fun send(project: Project, file: VirtualFile) {
        // Matches LspServerDescriptor.getFileUri's own construction (confirmed by decompiling
        // Rider 2024.3.5's actual bytecode) rather than a hand-rolled "file://" + path string, so
        // this lines up with whatever URI format the same LSP framework already used for this
        // file's textDocument/didOpen.
        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(file.path))
        ReqnrollDebugLogger.verbose("documentActivated: $uri")
        ReqnrollNotificationSender.sendDocumentActivated(project, DocumentActivatedParams(uri))
    }
}

/** Ported verbatim from DocumentActivationState.cs's four-phase design — see that file's remarks. */
internal enum class DocumentActivationPhase { NOT_SEEN, OPENED, ACTIVATION_PENDING, ACTIVATED }

internal enum class DocumentActivationAction { NONE, SEND_NOW }

/** Thread-safe per-file state machine tracking whether a document-activation notification is still owed. */
internal class DocumentActivationState {
    private val lock = Any()
    private val phases = HashMap<String, DocumentActivationPhase>()

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
        phases.remove(filePath)
        Unit
    }

    private fun getPhase(filePath: String) = phases[filePath] ?: DocumentActivationPhase.NOT_SEEN
}
