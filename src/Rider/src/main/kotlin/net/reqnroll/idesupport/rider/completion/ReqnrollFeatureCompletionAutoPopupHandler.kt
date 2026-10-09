package net.reqnroll.idesupport.rider.completion

import com.intellij.codeInsight.AutoPopupController
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiFile
import net.reqnroll.idesupport.rider.isFeatureExtension

/**
 * Starts the completion popup when one of the server's non-identifier trigger characters (`@` and
 * space, advertised by `CompletionHandler.TriggerCharacters`) is typed in a `.feature` file (#902).
 *
 * Rider's generic LSP client reads the server's `triggerCharacters` only to label a request that
 * something else already started (confirmed by decompiling `LspCompletionUtilKt` and
 * `LspCompletionContributor` in the real 2024.3.5 jar); nothing in the platform LSP package starts
 * a popup on them, and IntelliJ's own auto-popup fires on letters/digits only. Without this, tag and
 * step completion appear only after the first identifier keystroke (or on Ctrl+Space).
 *
 * This only schedules the popup — the request itself still goes through the platform's
 * `LspCompletionContributor`, and the server decides what (if anything) to offer at the position.
 */
class ReqnrollFeatureCompletionAutoPopupHandler : TypedHandlerDelegate() {
    override fun charTyped(c: Char, project: Project, editor: Editor, file: PsiFile): Result {
        if (!isAutoPopupTrigger(c)) return Result.CONTINUE

        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return Result.CONTINUE
        if (!isFeatureExtension(virtualFile.extension)) return Result.CONTINUE

        AutoPopupController.getInstance(project).scheduleAutoPopup(editor)
        return Result.CONTINUE
    }

    companion object {
        /** Mirrors the server's advertised `CompletionHandler.TriggerCharacters` (`@`, space). */
        internal fun isAutoPopupTrigger(c: Char): Boolean = c == '@' || c == ' '
    }
}
