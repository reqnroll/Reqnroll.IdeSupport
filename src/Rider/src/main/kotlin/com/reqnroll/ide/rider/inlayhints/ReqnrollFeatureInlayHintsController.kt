package com.reqnroll.ide.rider.inlayhints

import com.intellij.codeInsight.hint.TooltipController
import com.intellij.codeInsight.hint.TooltipGroup
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorCustomElementRenderer
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.Inlay
import com.intellij.openapi.editor.colors.EditorFontType
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.editor.event.EditorMouseEvent
import com.intellij.openapi.editor.event.EditorMouseMotionListener
import com.intellij.openapi.editor.markup.TextAttributes
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.ui.JBColor
import com.intellij.util.Alarm
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import org.eclipse.lsp4j.InlayHint
import java.awt.Graphics
import java.awt.Rectangle

/**
 * Renders binding-info inlay hints for `.feature` files directly against
 * [Editor.getInlayModel], bypassing IntelliJ's declarative inlay-hints framework entirely.
 *
 * That framework dispatches by PSI language (a [com.intellij.codeInsight.hints.declarative.InlayHintsProvider]
 * registers for one via `language=` in `plugin.xml`), but `.feature` files have no
 * [com.intellij.lang.ParserDefinition] registered for
 * [com.reqnroll.ide.rider.ReqnrollFeatureLanguage] — confirmed via decompiling
 * `PsiPlainTextFileImpl`/`PlainTextParserDefinition` that IntelliJ's built-in fallback for a
 * language with no parser hardcodes `PlainTextLanguage`, not the file's declared language. So a
 * declarative provider registered for "Reqnroll Feature" was silently never invoked — confirmed
 * live: zero `FeatureInlayHintHandler` requests ever reached the server, with no error anywhere.
 * Managing inlays here instead matches how every other `.feature` feature in this plugin already
 * works (semantic tokens, diagnostics, CodeLens): driven directly off the LSP request, keyed by
 * URI/[Editor], with no PSI dependency at all.
 *
 * Refresh has two triggers, neither of which needed a new custom `reqnroll`-prefixed protocol message:
 *  - [refreshOpenFeatureEditors], called from
 *    [com.reqnroll.ide.rider.lsp.ReqnrollInlayHintRefreshInterceptor] whenever the server sends
 *    the *standard* `workspace/inlayHint/refresh` request — purpose-built for exactly this, and
 *    already implemented server-side (`InlayHintRefreshHandler`), debounced, whenever binding
 *    discovery changes. Hooking that instead of e.g. the semantic-tokens refresh keeps the trigger
 *    thematically tied to what it's actually refreshing.
 *  - A short debounce on document edits, as a local fallback for the case where the user's own
 *    typing is what invalidated the hints (a pure client-side edit doesn't necessarily trigger a
 *    server-side semantic-tokens refresh on its own).
 *
 * Hover tooltip likewise needs manual glue: [EditorCustomElementRenderer] (unlike the declarative
 * framework's `InlayPresentation`) has no built-in hover/tooltip support at all — confirmed by
 * decompiling the interface, which only has paint/width/gutter-icon/context-menu hooks. An
 * [EditorMouseMotionListener] per editor session locates the hovered [Inlay] via
 * `InlayModel.getElementAt(Point)` and shows [BindingHintRenderer.tooltip] (already sent by the
 * server as `InlayHint.tooltip` — was simply never read client-side) via [TooltipController].
 */
class ReqnrollFeatureInlayHintsController : EditorFactoryListener {
    private class Session(
        val disposable: Disposable,
        val alarm: Alarm,
        val docListener: DocumentListener,
        val mouseMotionListener: EditorMouseMotionListener,
    )

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val project = editor.project ?: return
        val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!isFeatureExtension(virtualFile.extension)) return

        val disposable = Disposer.newDisposable("ReqnrollFeatureInlayHints:${virtualFile.path}")
        val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)

        val docListener = object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                alarm.cancelAllRequests()
                if (!alarm.isDisposed) alarm.addRequest({ refresh(project, editor, virtualFile) }, DEBOUNCE_MS)
            }
        }
        editor.document.addDocumentListener(docListener, disposable)

        val mouseMotionListener = object : EditorMouseMotionListener {
            override fun mouseMoved(e: EditorMouseEvent) {
                val tooltip = (editor.inlayModel.getElementAt(e.mouseEvent.point)?.renderer as? BindingHintRenderer)
                    ?.tooltip
                if (tooltip.isNullOrBlank()) {
                    TooltipController.getInstance().cancelTooltip(TOOLTIP_GROUP, e.mouseEvent, true)
                } else {
                    TooltipController.getInstance()
                        .showTooltip(editor, e.mouseEvent.point, tooltip, false, TOOLTIP_GROUP)
                }
            }
        }
        editor.addEditorMouseMotionListener(mouseMotionListener, disposable)

        editor.putUserData(SESSION_KEY, Session(disposable, alarm, docListener, mouseMotionListener))

        refresh(project, editor, virtualFile)
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        val editor = event.editor
        val session = editor.getUserData(SESSION_KEY) ?: return
        editor.putUserData(SESSION_KEY, null)
        Disposer.dispose(session.disposable)
        clearInlays(editor)
    }

    companion object {
        private const val DEBOUNCE_MS = 400
        private const val PADDING_PX = 4
        private val SESSION_KEY = Key.create<Session>("Reqnroll.FeatureInlayHints.Session")
        private val INLAYS_KEY = Key.create<MutableList<Inlay<*>>>("Reqnroll.FeatureInlayHints.Inlays")
        private val TOOLTIP_GROUP = TooltipGroup("Reqnroll.FeatureInlayHints", 0)

        /** Refreshes inlay hints for every currently open `.feature` editor belonging to [project]. */
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

            // ReqnrollRequestSender.inlayHint uses sendRequestSync, which blocks the calling
            // thread for up to INLAY_HINT_TIMEOUT_MS — refresh() is called from the EDT (both
            // editorCreated and the SWING_THREAD debounce alarm), so the request itself must run
            // on a background thread or every editor open / debounced keystroke freezes the UI.
            ApplicationManager.getApplication().executeOnPooledThread {
                if (project.isDisposed || editor.isDisposed) return@executeOnPooledThread

                val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(virtualFile.path))
                val hints = ReqnrollRequestSender.inlayHint(project, uri, 0, editor.document.lineCount)
                ReqnrollDebugLogger.info("ReqnrollFeatureInlayHintsController: ${hints?.size ?: "null"} hint(s) for $uri")

                ApplicationManager.getApplication().invokeLater(
                    {
                        if (!editor.isDisposed) renderInlays(editor, editor.document, hints.orEmpty())
                    },
                    ModalityState.any(),
                )
            }
        }

        private fun renderInlays(editor: Editor, document: Document, hints: List<InlayHint>) {
            clearInlays(editor)
            val inlays = mutableListOf<Inlay<*>>()

            for (hint in hints) {
                val line = hint.position.line
                if (line !in 0 until document.lineCount) continue
                val lineEndOffset = document.getLineEndOffset(line)
                val character = hint.position.character.coerceAtMost(lineEndOffset - document.getLineStartOffset(line))
                val offset = document.getLineStartOffset(line) + character
                val label = hint.label.left ?: continue
                val tooltip = hint.tooltip?.left

                val inlay =
                    editor.inlayModel.addInlineElement(offset, true, BindingHintRenderer(label, tooltip)) ?: continue
                inlays += inlay
            }

            editor.putUserData(INLAYS_KEY, inlays)
        }

        private fun clearInlays(editor: Editor) {
            editor.getUserData(INLAYS_KEY)?.forEach { Disposer.dispose(it) }
            editor.putUserData(INLAYS_KEY, null)
        }
    }

    /** Paints a single-line, greyed-out text label; [tooltip] (if any) is shown on hover — see the class doc's mouse-motion-listener note. */
    private class BindingHintRenderer(private val text: String, val tooltip: String?) : EditorCustomElementRenderer {
        override fun calcWidthInPixels(inlay: Inlay<*>): Int {
            val editor = inlay.editor
            val metrics = editor.contentComponent.getFontMetrics(editor.colorsScheme.getFont(EditorFontType.PLAIN))
            return metrics.stringWidth(text) + 2 * PADDING_PX
        }

        override fun paint(inlay: Inlay<*>, g: Graphics, targetRegion: Rectangle, textAttributes: TextAttributes) {
            val editor = inlay.editor
            g.font = editor.colorsScheme.getFont(EditorFontType.PLAIN)
            g.color = JBColor.GRAY
            val ascent = g.fontMetrics.ascent
            g.drawString(text, targetRegion.x + PADDING_PX, targetRegion.y + ascent)
        }
    }
}
