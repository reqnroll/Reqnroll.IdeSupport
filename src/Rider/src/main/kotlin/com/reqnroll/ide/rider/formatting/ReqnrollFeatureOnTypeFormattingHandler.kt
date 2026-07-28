package com.reqnroll.ide.rider.formatting

import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.psi.PsiFile
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.isDocumentStale
import org.eclipse.lsp4j.TextEdit

/**
 * Triggers server-side table-column realignment (`textDocument/onTypeFormatting`,
 * `GherkinFormattingHandler.cs`) when `|` is typed in a `.feature` file.
 *
 * Rider's platform has no generic client-side bridge for on-type formatting — unlike whole-document
 * formatting's `LspFormattingService` (see `ReqnrollLspServerDescriptor.lspFormattingSupport`),
 * confirmed by enumerating every class under `com.intellij.platform.lsp.api.customization` in the
 * real 2024.3.5 jar: nothing exists for it. So this is manual client glue, the same pattern already
 * used for CodeLens/inlay hints/semantic tokens elsewhere in this plugin.
 *
 * v1 scope: only the `|` trigger character (the primary table-editing signal — typing/removing a
 * cell delimiter). The server also registers `\n`/`\t` as trigger characters (see
 * `GherkinFormattingHandler`'s `MoreTriggerCharacter`), but those aren't routed through
 * [TypedHandlerDelegate.charTyped] at all — Enter and Tab go through separate extension points
 * (`EnterHandlerDelegate`, overriding the Tab action) with meaningfully more platform-specific
 * complexity than a single typed-character hook. Not implemented here.
 */
class ReqnrollFeatureOnTypeFormattingHandler : TypedHandlerDelegate() {
    override fun charTyped(c: Char, project: Project, editor: Editor, file: PsiFile): Result {
        if (c != '|') return Result.CONTINUE

        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return Result.CONTINUE
        if (virtualFile.extension != "feature") return Result.CONTINUE

        val document = editor.document
        val offset = editor.caretModel.offset
        val line = document.getLineNumber(offset)
        val character = offset - document.getLineStartOffset(line)
        val insertSpaces = !editor.settings.isUseTabCharacter(project)
        val tabSize = editor.settings.getTabSize(project)
        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(virtualFile.path))
        // Captured before the request fires so the edit-application step below can detect
        // whether the document changed in the meantime (issue #326) -- the server's returned
        // TextEdit line/character offsets are only valid against the document as it looked at
        // this instant, not whatever it looks like once the (up to ~10s) request returns.
        val requestModificationStamp = document.modificationStamp

        // charTyped runs on the EDT; ReqnrollRequestSender.onTypeFormatting uses sendRequestSync,
        // which blocks the calling thread for up to its timeout — the request must run on a
        // background thread, same lesson as ReqnrollFeatureInlayHintsController's earlier
        // EDT-blocking fix. Only the edit application (WriteCommandAction) goes back on the EDT.
        ApplicationManager.getApplication().executeOnPooledThread {
            if (project.isDisposed || editor.isDisposed) return@executeOnPooledThread

            val edits = ReqnrollRequestSender.onTypeFormatting(
                project, uri, line, character, "|", tabSize, insertSpaces,
            )
            ReqnrollDebugLogger.info(
                "ReqnrollFeatureOnTypeFormattingHandler: ${edits?.size ?: "null"} edit(s) for $uri",
            )
            if (edits.isNullOrEmpty()) return@executeOnPooledThread

            ApplicationManager.getApplication().invokeLater {
                if (project.isDisposed || editor.isDisposed) return@invokeLater
                if (isDocumentStale(editor.document.modificationStamp, requestModificationStamp)) {
                    ReqnrollDebugLogger.info(
                        "ReqnrollFeatureOnTypeFormattingHandler: document changed since the request " +
                            "was sent; discarding stale edits for $uri.",
                    )
                    return@invokeLater
                }
                applyEdits(project, editor.document, edits)
            }
        }

        return Result.CONTINUE
    }

    companion object {
        private fun applyEdits(project: Project, document: Document, edits: List<TextEdit>) {
            WriteCommandAction.runWriteCommandAction(project) {
                for (edit in orderForApplication(edits)) {
                    val startOffset = offsetOf(document, edit.range.start.line, edit.range.start.character)
                    val endOffset = offsetOf(document, edit.range.end.line, edit.range.end.character)
                    document.replaceString(startOffset, endOffset, edit.newText)
                }
            }
        }

        private fun offsetOf(document: Document, line: Int, character: Int): Int {
            if (line !in 0 until document.lineCount) return document.textLength
            val lineStart = document.getLineStartOffset(line)
            val lineEnd = document.getLineEndOffset(line)
            return (lineStart + character).coerceIn(lineStart, lineEnd)
        }

        /**
         * Sorts [edits] in reverse document order (latest start position first) so each edit's
         * offsets stay valid when applied in sequence, even though earlier edits in the *original*
         * list may otherwise shift later text — the edits themselves are assumed non-overlapping
         * (true for table-realignment edits from a single `onTypeFormatting` response). Pure and
         * `internal` so this ordering is unit-testable without a live `Document`.
         */
        internal fun orderForApplication(edits: List<TextEdit>): List<TextEdit> =
            edits.sortedWith(
                compareByDescending<TextEdit> { it.range.start.line }
                    .thenByDescending { it.range.start.character },
            )
    }
}
