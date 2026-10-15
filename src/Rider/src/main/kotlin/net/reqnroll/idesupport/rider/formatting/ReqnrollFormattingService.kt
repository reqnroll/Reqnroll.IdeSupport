package net.reqnroll.idesupport.rider.formatting

import com.intellij.formatting.service.AsyncDocumentFormattingService
import com.intellij.formatting.service.AsyncFormattingRequest
import com.intellij.formatting.service.FormattingService
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.progress.ProcessCanceledException
import com.intellij.psi.PsiFile
import com.intellij.platform.lsp.util.applyTextEdits
import net.reqnroll.idesupport.rider.actions.ReqnrollNotify
import net.reqnroll.idesupport.rider.isFeatureExtension
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.FormatOutcome
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri

/**
 * Whole-document Reformat Code for `.feature` files — replaces the platform's own
 * `LspFormattingService` (issue #1172). That service sends `textDocument/formatting` through
 * `LspServer.sendRequestSync`, which swallows JSON-RPC errors, so the server's `-32803` "edit rejected"
 * reason (issue #1132) never reached the user. This one uses the suspend `sendRequest` (see
 * [ReqnrollRequestSender.formatDocument]) and can therefore show it.
 *
 * The rejection is shown in a modal only when the reformat was explicitly requested by the user
 * ([ExplicitReformatTracker]); a rejection on an implicit trigger (format on save, on paste, ...) is
 * logged only. Like the platform service, it advertises no `FORMAT_FRAGMENTS`, so Reformat Selection
 * does not route here, and the platform one is no longer enabled (the descriptor has no
 * `lspFormattingSupport`), so the two never compete.
 */
class ReqnrollFormattingService : AsyncDocumentFormattingService() {
    override fun getFeatures(): Set<FormattingService.Feature> = emptySet()

    override fun canFormat(file: PsiFile): Boolean {
        val virtualFile = file.virtualFile ?: return false
        return virtualFile.isInLocalFileSystem && isFeatureExtension(virtualFile.extension) &&
            ReqnrollRequestSender.hasRunningServer(file.project)
    }

    override fun getName(): String = "Reqnroll"

    // The platform's notification group for LSP messages; no balloon is raised by this service itself
    // (failures are only logged), it is only needed to satisfy the base class.
    override fun getNotificationGroupId(): String = "LSP window/showMessage"

    override fun createFormattingTask(request: AsyncFormattingRequest): FormattingTask? {
        val file = request.context.virtualFile ?: return null
        val project = request.context.project
        return object : FormattingTask {
            override fun cancel(): Boolean = false

            override fun isRunUnderProgress(): Boolean = true

            override fun run() {
                val original = request.documentText
                try {
                    val fileType = file.fileType
                    val settings = request.context.codeStyleSettings
                    val outcome = ReqnrollRequestSender.formatDocument(
                        project,
                        localPathToLspUri(file.path),
                        settings.getIndentSize(fileType),
                        !settings.useTabCharacter(fileType),
                    )
                    when (outcome) {
                        is FormatOutcome.Edits -> request.onTextReady(applyEdits(original, outcome))
                        is FormatOutcome.Rejected -> {
                            if (ExplicitReformatTracker.instance.consume(file.path)) {
                                ApplicationManager.getApplication().invokeLater {
                                    if (!project.isDisposed)
                                        ReqnrollNotify.editRejected(project, outcome.message, "Reformat Code")
                                }
                            } else {
                                ReqnrollDebugLogger.info(
                                    "Reformat Code: rejected by server on an implicit trigger, not shown: ${outcome.message}")
                            }
                            request.onTextReady(original)
                        }
                        FormatOutcome.Failed -> request.onTextReady(original)
                    }
                } catch (ex: ProcessCanceledException) {
                    throw ex
                } catch (ex: Exception) {
                    ReqnrollDebugLogger.warn("Reformat Code: failed", ex)
                    request.onTextReady(original)
                }
            }
        }
    }

    private fun applyEdits(original: String, outcome: FormatOutcome.Edits): String {
        if (outcome.edits.isEmpty()) return original
        val document = EditorFactory.getInstance().createDocument(original)
        if (!applyTextEdits(document, outcome.edits)) {
            ReqnrollDebugLogger.warn("Reformat Code: the server's edits could not be applied; leaving the text unchanged")
            return original
        }
        return document.text
    }
}
