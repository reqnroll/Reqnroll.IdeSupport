package com.reqnroll.ide.rider.commenting

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender

/**
 * Comment/Uncomment toggle for `.feature` files (issue #159). Neither VS nor VS Code has a native
 * `#`-comment understanding of Gherkin either — both redirect their own comment-toggle shortcut
 * to the server's `workspace/executeCommand` (`reqnroll.toggleComment`), which replies with a
 * `workspace/applyEdit` the client's built-in LSP infrastructure applies natively. This mirrors
 * that design on Rider.
 *
 * **Not** an [com.intellij.openapi.editor.actionSystem.EditorActionHandler] decorating
 * `IdeActions.ACTION_COMMENT_LINE` — that was the first approach tried here and it doesn't work:
 * the actual `CommentByLineCommentAction` bound to that ID is a
 * `com.intellij.codeInsight.actions.MultiCaretCodeInsightAction`, not an `EditorAction`. It
 * hardcodes `new CommentByLineCommentHandler()` in its own `getHandler()` (ignoring
 * `EditorActionManager` entirely) and gates `isValidFor` on `LanguageCommenters.forLanguage`
 * finding a registered `Commenter` for the PSI file's language — confirmed by decompiling both
 * classes. `.feature` has no `Commenter` and no PSI language, so that action is simply disabled
 * for it and never reaches any wrapped `EditorActionHandler`.
 *
 * Instead, this is a plain [AnAction] bound to the *same* default `Ctrl+/` keystroke
 * (`plugin.xml`'s `<keyboard-shortcut>`), and [ReqnrollCommentTogglePromoter] suppresses the
 * built-in `CommentByLineComment` action from that keystroke's candidate list specifically for
 * `.feature` files, so this action fires instead without touching the shortcut for any other file
 * type.
 */
class ReqnrollToggleCommentAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible =
            e.project != null && e.getData(CommonDataKeys.EDITOR) != null &&
                file != null && file.extension.equals("feature", ignoreCase = true)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return

        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(file.path))
        val (startLine, endLine) = selectionLines(editor)

        ReqnrollDebugLogger.info(
            "ReqnrollToggleCommentAction: invoked for $uri, lines [$startLine..$endLine]", curated = true)

        // ReqnrollRequestSender.toggleComment uses sendRequestSync, which blocks the calling
        // thread — actionPerformed() runs on the EDT, so the request itself must run on a
        // background thread, matching every other Reqnroll request-sender caller's identical
        // rationale. The actual edit arrives asynchronously via the server's own
        // workspace/applyEdit request, applied natively by Rider's platform Lsp4jClient — this
        // action has nothing further to do once the command is dispatched.
        ApplicationManager.getApplication().executeOnPooledThread {
            val dispatched = ReqnrollRequestSender.toggleComment(project, uri, startLine, endLine)
            ReqnrollDebugLogger.info(
                "ReqnrollToggleCommentAction: toggleComment dispatched=$dispatched", curated = true)
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
