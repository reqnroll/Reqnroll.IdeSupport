package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.ide.CopyPasteManager
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.protocol.CopyToClipboardParams
import java.awt.datatransfer.StringSelection

/**
 * Puts the text of the server's `reqnroll/copyToClipboard` notifications on the IDE clipboard. The
 * server sends one when the user picks a "Copy step definition(s) to clipboard" quick fix: LSP has
 * no clipboard API, so the server relays the text back for the client to copy.
 */
object ServerClipboard {
    /** Passes the text of [params] to [copy]; a notification without text is ignored. Exposed for testing. */
    internal fun copy(params: CopyToClipboardParams?, copy: (text: String) -> Unit) {
        val text = params?.text
        if (text.isNullOrEmpty()) return
        copy(text)
    }

    /** Copies the text of [params] to the IDE clipboard, on the EDT. */
    fun copy(params: CopyToClipboardParams?) = copy(params) { text ->
        ApplicationManager.getApplication().invokeLater(
            {
                try {
                    CopyPasteManager.getInstance().setContents(StringSelection(text))
                } catch (e: Exception) {
                    ReqnrollDebugLogger.warn("Could not copy the step definition to the clipboard: ${e.message}")
                }
            },
            ModalityState.any(),
        )
    }
}
