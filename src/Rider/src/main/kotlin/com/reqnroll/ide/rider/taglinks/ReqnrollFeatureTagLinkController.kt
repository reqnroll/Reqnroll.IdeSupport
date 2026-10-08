package com.reqnroll.ide.rider.taglinks

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.EditorMouseEventArea
import com.intellij.openapi.editor.RangeMarker
import com.intellij.openapi.editor.colors.EditorColors
import com.intellij.openapi.editor.colors.EditorColorsManager
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.editor.event.EditorMouseEvent
import com.intellij.openapi.editor.event.EditorMouseListener
import com.intellij.openapi.editor.event.EditorMouseMotionListener
import com.intellij.openapi.editor.ex.EditorEx
import com.intellij.openapi.editor.markup.EffectType
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.editor.markup.TextAttributes
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.util.Alarm
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter
import java.awt.Cursor
import java.awt.Font
import java.awt.event.InputEvent
import java.awt.event.MouseEvent
import org.eclipse.lsp4j.DocumentLink
import com.reqnroll.ide.rider.lsp.localPathToLspUri

/**
 * Makes Gherkin tags that match a configured `Traceability.TagLinks` pattern clickable (issue #755): the server
 * answers `textDocument/documentLink`, but Rider's generic LSP client never requests it (the same class of gap as
 * folding, CodeLens and inlay hints), so this controller requests the links itself, tracks each as a
 * [com.intellij.openapi.editor.RangeMarker] (which follows edits until the next debounced refresh replaces it),
 * and handles Ctrl/Cmd+hover (hand cursor + underline) and Ctrl/Cmd+click (open in the browser).
 *
 * Only http(s) targets are opened - see [TagLinkSupport.isOpenableUrl]. Each followed link reports the
 * "TagLink command executed" telemetry event, which carries no properties (the URL and tag text are never sent).
 */
class ReqnrollFeatureTagLinkController : EditorFactoryListener {
    private class Session(
        val disposable: Disposable,
        val alarm: Alarm,
    )

    private class Links(val markers: List<Pair<RangeMarker, String>>)

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val project = editor.project ?: return
        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!isFeatureExtension(virtualFile.extension)) return

        val disposable = Disposer.newDisposable("ReqnrollFeatureTagLinks:${virtualFile.path}")
        val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)

        editor.document.addDocumentListener(object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                alarm.cancelAllRequests()
                if (!alarm.isDisposed) alarm.addRequest({ refresh(project, editor, virtualFile) }, DEBOUNCE_MS)
            }
        }, disposable)

        editor.addEditorMouseMotionListener(object : EditorMouseMotionListener {
            override fun mouseMoved(e: EditorMouseEvent) = updateHover(editor, e)
        }, disposable)
        editor.addEditorMouseListener(object : EditorMouseListener {
            override fun mouseClicked(e: EditorMouseEvent) = followLink(editor, e)
        }, disposable)

        editor.putUserData(SESSION_KEY, Session(disposable, alarm))
        refresh(project, editor, virtualFile)
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        val editor = event.editor
        val session = editor.getUserData(SESSION_KEY) ?: return
        editor.putUserData(SESSION_KEY, null)
        clearHover(editor)
        // The markers live on the document, which can outlive this editor (a second editor, a split), so release them.
        editor.getUserData(LINKS_KEY)?.markers?.forEach { it.first.dispose() }
        editor.putUserData(LINKS_KEY, null)
        Disposer.dispose(session.disposable)
    }

    companion object {
        private const val DEBOUNCE_MS = 400
        private val SESSION_KEY = Key.create<Session>("Reqnroll.TagLinks.Session")
        private val LINKS_KEY = Key.create<Links>("Reqnroll.TagLinks.Links")
        private val HOVER_KEY = Key.create<RangeHighlighter>("Reqnroll.TagLinks.Hover")

        /** Re-requests the links of every currently open `.feature` editor belonging to [project]. */
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

            // ReqnrollRequestSender.documentLink uses sendRequestSync, which blocks the calling thread, so the
            // request runs on a background thread - same rationale as ReqnrollFeatureFoldingController.
            ApplicationManager.getApplication().executeOnPooledThread {
                if (project.isDisposed || editor.isDisposed) return@executeOnPooledThread

                val uri = localPathToLspUri(virtualFile.path)
                val links = ReqnrollRequestSender.documentLink(project, uri)
                ReqnrollDebugLogger.verbose("ReqnrollFeatureTagLinkController: ${links?.size ?: "null"} link(s) for $uri")

                ApplicationManager.getApplication().invokeLater(
                    {
                        if (!editor.isDisposed) store(editor, links.orEmpty())
                    },
                    ModalityState.any(),
                )
            }
        }

        private fun store(editor: Editor, links: List<DocumentLink>) {
            val document = editor.document
            val ranges = TagLinkSupport.toRanges(links, document.textLength) { line ->
                if (line in 0 until document.lineCount) document.getLineStartOffset(line) else null
            }
            editor.getUserData(LINKS_KEY)?.markers?.forEach { it.first.dispose() }
            editor.putUserData(
                LINKS_KEY,
                Links(ranges.map { document.createRangeMarker(it.start, it.end) to it.target }),
            )
            clearHover(editor)
        }

        private fun linkAt(editor: Editor, offset: Int): TagLinkRange? {
            val markers = editor.getUserData(LINKS_KEY)?.markers ?: return null
            val ranges = markers.filter { it.first.isValid }
                .map { (marker, target) -> TagLinkRange(marker.startOffset, marker.endOffset, target) }
            return TagLinkSupport.linkAt(ranges, offset)
        }

        private fun isLinkModifierDown(e: MouseEvent): Boolean =
            (e.modifiersEx and (InputEvent.CTRL_DOWN_MASK or InputEvent.META_DOWN_MASK)) != 0

        private fun offsetAt(editor: Editor, e: EditorMouseEvent): Int? {
            val area = e.area
            if (area != EditorMouseEventArea.EDITING_AREA) return null
            return editor.logicalPositionToOffset(editor.xyToLogicalPosition(e.mouseEvent.point))
        }

        private fun updateHover(editor: Editor, e: EditorMouseEvent) {
            val offset = if (isLinkModifierDown(e.mouseEvent)) offsetAt(editor, e) else null
            val link = offset?.let { linkAt(editor, it) }
            if (link == null) {
                clearHover(editor)
                return
            }
            if (editor.getUserData(HOVER_KEY) != null) return

            val color = EditorColorsManager.getInstance().globalScheme.getAttributes(EditorColors.REFERENCE_HYPERLINK_COLOR)?.foregroundColor
                ?: editor.colorsScheme.defaultForeground
            val attributes = TextAttributes(null, null, color, EffectType.LINE_UNDERSCORE, Font.PLAIN)
            val highlighter = editor.markupModel.addRangeHighlighter(
                link.start, link.end, HighlighterLayer.SELECTION - 1, attributes, HighlighterTargetArea.EXACT_RANGE,
            )
            editor.putUserData(HOVER_KEY, highlighter)
            (editor as? EditorEx)?.setCustomCursor(this::class.java, Cursor.getPredefinedCursor(Cursor.HAND_CURSOR))
        }

        private fun clearHover(editor: Editor) {
            val highlighter = editor.getUserData(HOVER_KEY) ?: return
            editor.putUserData(HOVER_KEY, null)
            if (!editor.isDisposed) {
                editor.markupModel.removeHighlighter(highlighter)
                (editor as? EditorEx)?.setCustomCursor(this::class.java, null)
            }
        }

        private fun followLink(editor: Editor, e: EditorMouseEvent) {
            if (e.mouseEvent.button != MouseEvent.BUTTON1 || !isLinkModifierDown(e.mouseEvent)) return
            val offset = offsetAt(editor, e) ?: return
            val target = linkAt(editor, offset)?.target ?: return
            if (!TagLinkSupport.isOpenableUrl(target)) return

            e.consume()
            RiderTelemetryTransmitter.transmit(RiderTelemetryTransmitter.TAG_LINK_COMMAND_EXECUTED, emptyMap())
            ReqnrollDebugLogger.verbose("ReqnrollFeatureTagLinkController: opening tag link '$target'")
            BrowserUtil.browse(target)
        }
    }
}
