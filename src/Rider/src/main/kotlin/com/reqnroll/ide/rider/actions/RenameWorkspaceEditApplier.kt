package com.reqnroll.ide.rider.actions

import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFileManager
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import org.eclipse.lsp4j.TextEdit
import org.eclipse.lsp4j.WorkspaceEdit

/**
 * Applies the `WorkspaceEdit` returned by `textDocument/rename` to local IDE state. Rider has no
 * native rename bridge (confirmed by decompiling `LspServerDescriptor` — no `lspRenameSupport`-style
 * customization exists), and the server only proactively pushes `workspace/applyEdit` for Visual
 * Studio (see `RenamePostApplyCoordinator.PushEditIfVisualStudioAsync`) — every other client,
 * Rider included, is expected to apply the edit it gets back from the `rename` response itself.
 */
object RenameWorkspaceEditApplier {
    /** Resolves each touched URI's `Document` and applies its edits inside one write command. */
    fun apply(project: Project, edit: WorkspaceEdit) {
        val byUri = editsByUri(edit)
        if (byUri.isEmpty()) return

        WriteCommandAction.runWriteCommandAction(project) {
            for ((uri, edits) in byUri) {
                val document = documentForUri(uri)
                if (document == null) {
                    ReqnrollDebugLogger.warn("RenameWorkspaceEditApplier: could not resolve document for $uri")
                    continue
                }
                applyEdits(document, edits)
            }
        }
    }

    /** `internal` so callers (e.g. [RenameStepRunner]) can capture/compare a document's [Document.modificationStamp] around a request, without duplicating this URI-to-Document lookup. */
    internal fun documentForUri(uri: String): Document? {
        val file = VirtualFileManager.getInstance().findFileByUrl(uri) ?: return null
        return FileDocumentManager.getInstance().getDocument(file)
    }

    private fun applyEdits(document: Document, edits: List<TextEdit>) {
        for (edit in orderForApplication(edits)) {
            val startOffset = offsetOf(document, edit.range.start.line, edit.range.start.character)
            val endOffset = offsetOf(document, edit.range.end.line, edit.range.end.character)
            document.replaceString(startOffset, endOffset, edit.newText)
        }
    }

    private fun offsetOf(document: Document, line: Int, character: Int): Int {
        if (line !in 0 until document.lineCount) return document.textLength
        val lineStart = document.getLineStartOffset(line)
        val lineEnd = document.getLineEndOffset(line)
        return (lineStart + character).coerceIn(lineStart, lineEnd)
    }

    /**
     * Sorts [edits] in reverse document order so each edit's offsets stay valid when applied in
     * sequence within a single file — same technique (and same non-overlap assumption) as
     * `ReqnrollFeatureOnTypeFormattingHandler.orderForApplication`. `internal` for the same
     * reason as that counterpart: unit-testable without a live `Document`.
     */
    internal fun orderForApplication(edits: List<TextEdit>): List<TextEdit> =
        edits.sortedWith(
            compareByDescending<TextEdit> { it.range.start.line }
                .thenByDescending { it.range.start.character },
        )

    /**
     * Parses an LSP4J `WorkspaceEdit` into a per-URI edit list, checking `documentChanges` first
     * and falling back to the legacy `changes` map — defensive, since this plugin doesn't control
     * exactly which `workspace.workspaceEdit` capabilities Rider's platform LSP client advertises
     * to the server (unlike VS Code, which explicitly enables `documentChanges`, or VS, which the
     * server special-cases to the legacy shape via `ClientIdeContext.IsVisualStudio`).
     *
     * `internal` so it's unit-testable with plain LSP4J POJOs, without a platform `Document` fixture.
     */
    internal fun editsByUri(edit: WorkspaceEdit): Map<String, List<TextEdit>> {
        val documentChanges = edit.documentChanges
        if (!documentChanges.isNullOrEmpty()) {
            return documentChanges
                .filter { it.isLeft }
                .map { it.left }
                .groupBy({ it.textDocument.uri }, { it.edits })
                .mapValues { (_, editLists) -> editLists.flatten() }
        }

        return edit.changes?.mapValues { (_, edits) -> edits } ?: emptyMap()
    }
}
