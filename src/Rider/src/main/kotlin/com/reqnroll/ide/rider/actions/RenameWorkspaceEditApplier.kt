package com.reqnroll.ide.rider.actions

import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.ReadonlyStatusHandler
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.lspUriToLocalPath
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
    /**
     * Resolves each touched URI's `Document` and applies its edits inside one write command,
     * returning whether the edit was actually applied.
     *
     * Before any document is touched, the read-only status of every target file is checked and the
     * whole edit is abandoned (returning `false`) if any of them is read-only. Without that check a
     * read-only file only failed when its own turn came to be written — *after* earlier documents
     * in the same `WorkspaceEdit` had already been mutated — leaving a half-applied rename, and the
     * exception propagated out of [apply] so the caller's `renameApplied=false` report was never
     * sent and the server's staged update hung (issue #995). The write is additionally wrapped by
     * [applyGuarded] so any other mid-apply failure (offset out of range, I/O error) also returns
     * `false` instead of propagating, reaching the same "reported" state rather than the dangling
     * one.
     */
    fun apply(project: Project, edit: WorkspaceEdit): Boolean {
        val byUri = editsByUri(edit)
        if (byUri.isEmpty()) return true

        return applyGuarded(
            ensureWritable = { ensureTargetsWritable(project, byUri.keys) },
            write = {
                WriteCommandAction.runWriteCommandAction(project) {
                    for ((uri, edits) in byUri) {
                        val document = documentForUri(uri)
                        if (document == null) {
                            ReqnrollDebugLogger.warn(
                                "RenameWorkspaceEditApplier: could not resolve document for $uri")
                            continue
                        }
                        applyEdits(document, edits)
                    }
                }
            },
        )
    }

    /**
     * Runs [write] only after [ensureWritable] reports the targets writable, swallows any exception
     * [write] throws, and returns whether the edit was applied.
     *
     * `internal` so the read-only abort and the mid-apply-failure paths are unit-testable with plain
     * lambdas, without a platform `Project` fixture — Rider's project services refuse to initialize
     * in a bare fixture project (see `ReqnrollFeatureFileTypeRegistrationTest`).
     */
    internal fun applyGuarded(ensureWritable: () -> Boolean, write: () -> Unit): Boolean {
        if (!ensureWritable()) return false
        return try {
            write()
            true
        } catch (e: Exception) {
            ReqnrollDebugLogger.warn(
                "RenameWorkspaceEditApplier: applying the rename failed; reporting applied=false", e)
            false
        }
    }

    /**
     * True when every file the edit targets is (or can be made) writable. Files that can't be
     * resolved in the VFS are ignored: [documentForUri] already skips an unresolvable document, and
     * a read-only prompt must not be raised for a path we can't even see.
     *
     * [ReadonlyStatusHandler.ensureFilesWritable] is what surfaces the platform's own "clear
     * read-only status?" prompt (and any project-level override); when even after that the file is
     * still read-only it reports the remaining read-only files, which is our cue to abort.
     */
    private fun ensureTargetsWritable(project: Project, uris: Set<String>): Boolean {
        val files = uris
            .mapNotNull { uri -> lspUriToLocalPath(uri) }
            .mapNotNull { path -> LocalFileSystem.getInstance().findFileByPath(path) }
            .filter { it.isValid }
            .toTypedArray()

        if (files.isEmpty()) return true

        val status = ReadonlyStatusHandler.getInstance(project).ensureFilesWritable(*files)
        if (!status.hasReadonlyFiles()) return true

        ReqnrollDebugLogger.warn(
            "RenameWorkspaceEditApplier: refusing to apply rename; read-only target file(s): " +
                status.readonlyFiles.joinToString { it.path })
        return false
    }

    /** `internal` so callers (e.g. [RenameStepRunner]) can reuse this URI-to-Document lookup. */
    internal fun documentForUri(uri: String): Document? {
        val path = lspUriToLocalPath(uri) ?: return null
        val file = LocalFileSystem.getInstance().refreshAndFindFileByPath(path) ?: return null
        return FileDocumentManager.getInstance().getDocument(file)
    }

    /**
     * [documentForUri] without the VFS refresh — for reading a [Document.modificationStamp] to
     * compare against a previously captured one (issue #671, R4).
     *
     * `refreshAndFindFileByPath` performs a synchronous VFS refresh, and a refresh that decides the
     * file changed on disk makes `FileDocumentManager` reload the document, which assigns a **new
     * modification stamp even when the content is byte-identical**. Using it on both sides of a
     * staleness comparison therefore manufactures the very "the file changed" verdict the
     * comparison exists to detect — a false positive that discarded the user's rename and left the
     * server's binding registry describing a step that exists in no file (issue #670). Bind-mounted
     * workspaces (the Rider devcontainer's Windows-host mount) make this markedly more likely,
     * since their mtimes are not reliably stable.
     *
     * Nothing is applied through this lookup, so skipping the refresh costs nothing: a document
     * that is not already in the VFS has no stamp worth comparing anyway.
     */
    internal fun documentForUriWithoutRefresh(uri: String): Document? {
        val path = lspUriToLocalPath(uri) ?: return null
        val file = LocalFileSystem.getInstance().findFileByPath(path) ?: return null
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
     * server special-cases to the legacy shape via `IdeBehaviours.AppliesRenameResponseEditNatively`).
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
