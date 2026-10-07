package com.reqnroll.ide.rider.taglinks

import com.intellij.codeInsight.hint.HintManager
import com.intellij.codeInsight.hint.HintManagerImpl
import com.intellij.codeInsight.hint.HintUtil
import com.intellij.ide.BrowserUtil
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.EditorMouseEventArea
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
import com.intellij.openapi.util.SystemInfo
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.ui.LightweightHint
import com.intellij.util.Alarm
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter
import java.awt.Cursor
import java.awt.Font
import java.awt.Point
import java.awt.event.InputEvent
import java.awt.event.MouseEvent
import org.eclipse.lsp4j.DocumentLink

/**
 * Makes Gherkin tags that match a configured `Traceability.TagLinks` pattern clickable (issue #755): the server
 * answers `textDocument/documentLink`, but Rider's generic LSP client never requests it (the same class of gap as
 * folding, CodeLens and inlay hints), so this controller requests the links itself.
 *
 * Each link is shown **permanently** as a link (issue #921): a range highlighter in the hyperlink colour with an
 * underline, which follows edits until the next debounced refresh replaces it, so it is visible which tags are
 * clickable without holding a key. Hovering one (no modifier needed, after a short delay) shows its target and how
 * to follow it. Ctrl/Cmd+hover adds the hand cursor and Ctrl/Cmd+click opens the link in the browser.
 *
 * Only http(s) targets are opened - see [TagLinkSupport.isOpenableUrl]. Each followed link reports the
 * "TagLink command executed" telemetry event, which carries no properties (the URL and tag text are never sent).
 */
class ReqnrollFeatureTagLinkController : EditorFactoryListener {
    private class Session(
        val disposable: Disposable,
        val alarm: Alarm,
        val hoverAlarm: Alarm,
    )

    private class Links(val highlighters: List<Pair<RangeHighlighter, String>>)

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val project = editor.project ?: return
        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!isFeatureExtension(virtualFile.extension)) return

        val disposable = Disposer.newDisposable("ReqnrollFeatureTagLinks:${virtualFile.path}")
        val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)
        val hoverAlarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)

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
            override fun mouseExited(e: EditorMouseEvent) = clearHover(editor)
        }, disposable)

        editor.putUserData(SESSION_KEY, Session(disposable, alarm, hoverAlarm))
        refresh(project, editor, virtualFile)
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        val editor = event.editor
        val session = editor.getUserData(SESSION_KEY) ?: return
        clearHover(editor)
        editor.putUserData(SESSION_KEY, null)
        releaseLinks(editor)
        Disposer.dispose(session.disposable)
    }

    companion object {
        private const val DEBOUNCE_MS = 400
        private const val HOVER_DELAY_MS = 500
        private val SESSION_KEY = Key.create<Session>("Reqnroll.TagLinks.Session")
        private val LINKS_KEY = Key.create<Links>("Reqnroll.TagLinks.Links")
        private val HOVER_LINK_KEY = Key.create<TagLinkRange>("Reqnroll.TagLinks.HoverLink")
        private val HOVER_HINT_KEY = Key.create<LightweightHint>("Reqnroll.TagLinks.HoverHint")
        private val CURSOR_KEY = Key.create<Boolean>("Reqnroll.TagLinks.HandCursor")

        /** Above syntax and semantic-token highlighting, so the link colour wins over the tag's own colour. */
        private const val LINK_LAYER = HighlighterLayer.SELECTION - 1

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

                val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(virtualFile.path))
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
            releaseLinks(editor)
            val attributes = linkAttributes(editor)
            editor.putUserData(
                LINKS_KEY,
                Links(
                    ranges.map {
                        editor.markupModel.addRangeHighlighter(
                            it.start, it.end, LINK_LAYER, attributes, HighlighterTargetArea.EXACT_RANGE,
                        ) to it.target
                    },
                ),
            )
            clearHover(editor)
        }

        /** Hyperlink colour (with an underline), taken from the active colour scheme. */
        private fun linkAttributes(editor: Editor): TextAttributes {
            val color = EditorColorsManager.getInstance().globalScheme
                .getAttributes(EditorColors.REFERENCE_HYPERLINK_COLOR)?.foregroundColor
                ?: editor.colorsScheme.defaultForeground
            return TextAttributes(color, null, color, EffectType.LINE_UNDERSCORE, Font.PLAIN)
        }

        private fun releaseLinks(editor: Editor) {
            val links = editor.getUserData(LINKS_KEY) ?: return
            editor.putUserData(LINKS_KEY, null)
            if (!editor.isDisposed) links.highlighters.forEach { editor.markupModel.removeHighlighter(it.first) }
        }

        private fun linkAt(editor: Editor, offset: Int): TagLinkRange? {
            val highlighters = editor.getUserData(LINKS_KEY)?.highlighters ?: return null
            val ranges = highlighters.filter { it.first.isValid }
                .map { (highlighter, target) -> TagLinkRange(highlighter.startOffset, highlighter.endOffset, target) }
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
            val link = offsetAt(editor, e)?.let { linkAt(editor, it) }
            setHandCursor(editor, link != null && isLinkModifierDown(e.mouseEvent))
            if (link == null) {
                clearHover(editor)
                return
            }
            if (editor.getUserData(HOVER_LINK_KEY)?.start == link.start) return

            clearHover(editor)
            editor.putUserData(HOVER_LINK_KEY, link)
            val session = editor.getUserData(SESSION_KEY) ?: return
            val point = Point(e.mouseEvent.point)
            if (!session.hoverAlarm.isDisposed) {
                session.hoverAlarm.addRequest({ showHoverHint(editor, link, point) }, HOVER_DELAY_MS)
            }
        }

        private fun showHoverHint(editor: Editor, link: TagLinkRange, point: Point) {
            if (editor.isDisposed || editor.getUserData(HOVER_LINK_KEY) != link) return

            val modifier = if (SystemInfo.isMac) "Cmd" else "Ctrl"
            val hint = LightweightHint(HintUtil.createInformationLabel(TagLinkSupport.hoverHtml(link.target, modifier)))
            editor.putUserData(HOVER_HINT_KEY, hint)
            HintManagerImpl.getInstanceImpl().showEditorHint(
                hint,
                editor,
                Point(point.x, point.y + editor.lineHeight),
                HintManager.HIDE_BY_ANY_KEY or HintManager.HIDE_BY_TEXT_CHANGE or HintManager.HIDE_BY_SCROLLING,
                0,
                false,
            )
        }

        private fun clearHover(editor: Editor) {
            editor.getUserData(SESSION_KEY)?.hoverAlarm?.cancelAllRequests()
            editor.putUserData(HOVER_LINK_KEY, null)
            editor.getUserData(HOVER_HINT_KEY)?.hide()
            editor.putUserData(HOVER_HINT_KEY, null)
            setHandCursor(editor, false)
        }

        private fun setHandCursor(editor: Editor, on: Boolean) {
            if ((editor.getUserData(CURSOR_KEY) == true) == on) return
            editor.putUserData(CURSOR_KEY, on)
            if (!editor.isDisposed) {
                (editor as? EditorEx)?.setCustomCursor(
                    this::class.java,
                    if (on) Cursor.getPredefinedCursor(Cursor.HAND_CURSOR) else null,
                )
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
