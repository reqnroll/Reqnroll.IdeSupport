package net.reqnroll.idesupport.rider.commenting

import com.intellij.codeInsight.generation.actions.CommentByLineCommentAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Editor
import net.reqnroll.idesupport.rider.actions.ReqnrollNotify
import net.reqnroll.idesupport.rider.isFeatureExtension
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.ToggleCommentOutcome
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri

/**
 * Comment/Uncomment toggle for `.feature` files (issues #159, #1151). Neither VS nor VS Code has a
 * native `#`-comment understanding of Gherkin either — both redirect the IDE's own comment command
 * to the server's `workspace/executeCommand` (`reqnroll.toggleComment`), which replies with a
 * `workspace/applyEdit` the client's built-in LSP infrastructure applies natively. This mirrors
 * that design on Rider.
 *
 * It intercepts the *command*, not a keystroke: `plugin.xml` registers this class with
 * `overrides="true"` under the platform's own `CommentByLineComment` action ID, which is what every
 * keymap binds its comment shortcuts to (`Ctrl+/` in the default keymap; `Ctrl+K,C`, `Ctrl+K,U`
 * and friends in the Visual Studio keymaps). So whichever keymap the user runs, and however they
 * rebind it, the shortcut reaches this class — nothing here names a key. Anywhere but a `.feature`
 * file it is the platform's [CommentByLineCommentAction] unchanged.
 *
 * Decorating the action's `EditorActionHandler` does not work, and neither does an `AnAction` that
 * merely competes for one keystroke (the earlier approach): the platform action is a
 * `MultiCaretCodeInsightAction` that hardcodes its own handler and gates `isValidFor` on
 * `LanguageCommenters.forLanguage` finding a `Commenter` for the PSI file's language — `.feature`
 * has no `Commenter` and no PSI language, so the platform action is simply disabled there.
 */
class ReqnrollCommentByLineCommentAction : CommentByLineCommentAction() {
    override fun update(e: AnActionEvent) {
        if (!isFeatureEditor(e)) {
            super.update(e)
            return
        }
        e.presentation.isEnabledAndVisible = e.project != null && e.getData(CommonDataKeys.EDITOR) != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        if (!isFeatureEditor(e)) {
            super.actionPerformed(e)
            return
        }
        toggleFeatureComment(e)
    }

    private fun isFeatureEditor(e: AnActionEvent): Boolean =
        isFeatureExtension(e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension)

    private fun toggleFeatureComment(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return

        val uri = localPathToLspUri(file.path)
        val (startLine, endLine) = selectionLines(editor)

        ReqnrollDebugLogger.info(
            "ReqnrollCommentByLineCommentAction: invoked for $uri, lines [$startLine..$endLine]")

        // ReqnrollRequestSender.toggleComment blocks the calling thread — actionPerformed() runs on
        // the EDT, so the request itself must run on a background thread, matching every other
        // Reqnroll request-sender caller's identical rationale. The actual edit arrives
        // asynchronously via the server's own workspace/applyEdit request, applied natively by
        // Rider's platform Lsp4jClient — once the command is dispatched there is nothing further to
        // do, except show the reason when the server rejects it (issue #1132).
        ApplicationManager.getApplication().executeOnPooledThread {
            val outcome = ReqnrollRequestSender.toggleComment(project, uri, startLine, endLine)
            ReqnrollDebugLogger.info(
                "ReqnrollCommentByLineCommentAction: toggleComment outcome=$outcome")
            if (outcome is ToggleCommentOutcome.Rejected) {
                ApplicationManager.getApplication().invokeLater {
                    if (!project.isDisposed) {
                        ReqnrollNotify.editRejected(project, outcome.message, "Comment/Uncomment")
                    }
                }
            }
        }
    }

    companion object {
        private fun selectionLines(editor: Editor): Pair<Int, Int> {
            val document = editor.document
            val caret = editor.caretModel.primaryCaret
            if (!caret.hasSelection()) {
                val line = document.getLineNumber(caret.offset)
                return line to line
            }

            val startLine = document.getLineNumber(caret.selectionStart)
            val endOffset = caret.selectionEnd
            val endLineRaw = document.getLineNumber(endOffset)
            val endChar = endOffset - document.getLineStartOffset(endLineRaw)
            return normalizeSelectionLines(startLine, endLineRaw, endChar)
        }

        /**
         * Mirrors VS Code's `normalizeSelectionLines` (`selectionUtils.ts`): a selection dragged
         * down to column 0 of a line without selecting any character on it (common when
         * extending a selection with Shift+Down) reports that line as the end line, but the user
         * didn't mean to include it — drop it back to the previous line. `internal` (rather than
         * private) purely so it's unit-testable without a real [Editor]/[com.intellij.openapi.editor.Document].
         */
        internal fun normalizeSelectionLines(startLine: Int, endLine: Int, endChar: Int): Pair<Int, Int> =
            if (endChar == 0 && endLine > startLine) startLine to endLine - 1 else startLine to endLine
    }
}
