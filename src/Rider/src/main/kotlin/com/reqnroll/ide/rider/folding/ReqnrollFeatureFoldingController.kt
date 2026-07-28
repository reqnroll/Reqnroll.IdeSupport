package com.reqnroll.ide.rider.folding

import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.FoldRegion
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.Alarm
import com.reqnroll.ide.rider.isFeatureExtension
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import org.eclipse.lsp4j.FoldingRange

/**
 * Renders Code Folding for `.feature` files directly against [Editor.getFoldingModel], bypassing
 * IntelliJ's PSI-based `com.intellij.lang.folding.FoldingBuilder` extension point entirely — same
 * reasoning as [com.reqnroll.ide.rider.inlayhints.ReqnrollFeatureInlayHintsController]'s doc
 * comment: `.feature` files have no [com.intellij.lang.ParserDefinition] registered, so a
 * language-scoped folding builder would never be invoked. Confirmed via bytecode inspection of
 * the pinned Rider 2024.3.5 `com.intellij.platform.lsp.api.LspServerDescriptor` (issue #162) that
 * there is also no generic-client `LspFoldingRangeSupport`-style opt-in for `textDocument/foldingRange`
 * — same class of gap as CodeLens/inlay hints/on-type formatting, all of which needed the same
 * kind of manual client-side glue.
 *
 * [FoldRegion] (via [com.intellij.openapi.editor.RangeMarker]) is a [com.intellij.openapi.util.UserDataHolder],
 * so regions created here are tagged with [REQNROLL_REGION_KEY] to distinguish them from any
 * region another provider might add to the same editor, and their expand/collapse state is
 * preserved by (start,end) offset across a debounced-edit rebuild — otherwise every keystroke
 * would silently re-expand everything the user had manually collapsed.
 *
 * [refreshOpenFeatureEditors] is called from [com.reqnroll.ide.rider.lsp.ReqnrollInlayHintRefreshInterceptor]
 * alongside its inlay-hints refresh — folding has no LSP `refresh` request of its own to piggyback
 * on, but needs the same re-query-once-the-server-is-actually-up trigger: `editorCreated` fires
 * (and issues the first `textDocument/foldingRange` request) as soon as a `.feature` file is
 * opened, which on solution/IDE startup happens well before the LSP server process exists —
 * confirmed live, the first request always came back null. With no further document edits there
 * was otherwise nothing to retry it, leaving folding permanently empty for files left open since
 * startup (issue #162 follow-up).
 */
class ReqnrollFeatureFoldingController : EditorFactoryListener {
    private class Session(
        val disposable: Disposable,
        val alarm: Alarm,
        val docListener: DocumentListener,
    )

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val project = editor.project ?: return
        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!isFeatureExtension(virtualFile.extension)) return

        val disposable = Disposer.newDisposable("ReqnrollFeatureFolding:${virtualFile.path}")
        val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)

        val docListener = object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                alarm.cancelAllRequests()
                if (!alarm.isDisposed) alarm.addRequest({ refresh(project, editor, virtualFile) }, DEBOUNCE_MS)
            }
        }
        editor.document.addDocumentListener(docListener, disposable)

        editor.putUserData(SESSION_KEY, Session(disposable, alarm, docListener))

        refresh(project, editor, virtualFile)
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        val editor = event.editor
        val session = editor.getUserData(SESSION_KEY) ?: return
        editor.putUserData(SESSION_KEY, null)
        Disposer.dispose(session.disposable)
    }

    companion object {
        private const val DEBOUNCE_MS = 400
        private const val PLACEHOLDER = "..."
        private val SESSION_KEY = Key.create<Session>("Reqnroll.FeatureFolding.Session")
        private val REQNROLL_REGION_KEY = Key.create<Boolean>("Reqnroll.FeatureFolding.Region")

        /** Refreshes folding ranges for every currently open `.feature` editor belonging to [project]. */
        fun refreshOpenFeatureEditors(project: Project) {
            for (editor in EditorFactory.getInstance().allEditors) {
                if (editor.project != project) continue
                val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: continue
                if (!isFeatureExtension(virtualFile.extension)) continue
                refresh(project, editor, virtualFile)
            }
        }

        private fun refresh(project: Project, editor: Editor, virtualFile: VirtualFile) {
            if (project.isDisposed || editor.isDisposed) return

            // ReqnrollRequestSender.foldingRange uses sendRequestSync, which blocks the calling
            // thread — refresh() runs on the EDT (editorCreated and the SWING_THREAD debounce
            // alarm both dispatch there), so the request itself must run on a background thread,
            // matching ReqnrollFeatureInlayHintsController's identical rationale.
            ApplicationManager.getApplication().executeOnPooledThread {
                if (project.isDisposed || editor.isDisposed) return@executeOnPooledThread

                val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(virtualFile.path))
                val ranges = ReqnrollRequestSender.foldingRange(project, uri)
                ReqnrollDebugLogger.info("ReqnrollFeatureFoldingController: ${ranges?.size ?: "null"} range(s) for $uri")

                ApplicationManager.getApplication().invokeLater(
                    {
                        if (!editor.isDisposed) renderFoldRegions(editor, editor.document, ranges.orEmpty())
                    },
                    ModalityState.any(),
                )
            }
        }

        private fun renderFoldRegions(editor: Editor, document: Document, ranges: List<FoldingRange>) {
            val foldingModel = editor.foldingModel

            val previousState: Map<Pair<Int, Int>, Boolean> = foldingModel.allFoldRegions
                .asSequence()
                .filter { it.getUserData(REQNROLL_REGION_KEY) == true }
                .associate { (it.startOffset to it.endOffset) to it.isExpanded }

            foldingModel.runBatchFoldingOperation {
                foldingModel.allFoldRegions
                    .filter { it.getUserData(REQNROLL_REGION_KEY) == true }
                    .forEach(foldingModel::removeFoldRegion)

                for (range in ranges) {
                    val offsets = toOffsets(document, range) ?: continue
                    val region: FoldRegion =
                        foldingModel.addFoldRegion(offsets.first, offsets.second, PLACEHOLDER) ?: continue
                    region.putUserData(REQNROLL_REGION_KEY, true)
                    previousState[offsets]?.let { region.isExpanded = it }
                }
            }
        }

        /** From a 0-based, inclusive [FoldingRange] line pair to a valid (startOffset, endOffset) document range — end of [FoldingRange.getStartLine] through end of [FoldingRange.getEndLine], keeping the start line's own text visible next to the placeholder. Returns null for out-of-range or degenerate (empty) spans. */
        private fun toOffsets(document: Document, range: FoldingRange): Pair<Int, Int>? {
            if (!isFoldable(document.lineCount, range.startLine, range.endLine)) return null

            val startOffset = document.getLineEndOffset(range.startLine)
            val endOffset = document.getLineEndOffset(range.endLine)
            if (endOffset <= startOffset) return null

            return startOffset to endOffset
        }

        /** `internal` (rather than private) purely so it's unit-testable without a real [Document]. */
        internal fun isFoldable(lineCount: Int, startLine: Int, endLine: Int): Boolean =
            startLine in 0 until lineCount && endLine in 0 until lineCount && endLine > startLine
    }
}
