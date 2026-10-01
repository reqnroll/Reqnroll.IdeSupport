package com.reqnroll.ide.rider.testrunner

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.ex.MarkupModelEx
import com.intellij.openapi.editor.markup.GutterIconRenderer
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.io.FileUtil
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.lspUriToLocalPath
import org.eclipse.lsp4j.DocumentSymbol
import java.util.concurrent.ConcurrentHashMap
import javax.swing.Icon

/** One failed-step gutter mark: the 0-based [line] of the failed step and the hover [tooltip]. */
internal data class FailedStepMark(val line: Int, val tooltip: String)

/**
 * Failed-step gutter marks in `.feature` editors (issue #451): after a Run lens run, a red
 * test-failed icon next to the step that failed, with a hover tooltip carrying the traced step and
 * error text - the low-noise gutter-icon-plus-tooltip presentation the legacy Reqnroll.Rider plugin
 * used (design doc section 6, "Presentation target"), not a squiggle or a line background.
 *
 * `.feature` has no PSI in this plugin (see [com.reqnroll.ide.rider.ReqnrollFeatureLanguage]), so
 * `LineMarkerProvider`/annotators are unavailable; the mark is a [RangeHighlighter] with a
 * [GutterIconRenderer] added straight to each open editor's markup model. The step is located from
 * the server's step trace (`failedStepIndex`) via [FailedStepLocator], never from a stack trace.
 *
 * Marks are in-memory, per-IDE-session, like [RunTestResultStore]. A scenario's marks are replaced
 * when it is next run, and - because the traced step index describes the file as it was run - all
 * of a file's marks are dropped on the first edit to its document.
 */
internal object FailedStepGutterMarks {
    private class Applied(val markupModel: MarkupModelEx, val highlighter: RangeHighlighter)

    /** Highlighters per (file URI, scenario header line) so a re-run replaces just its own marks. */
    private val applied = ConcurrentHashMap<Pair<String, Int>, List<Applied>>()

    /** Edit-listener lifetimes per file URI: disposed once the file's marks are cleared. */
    private val editListeners = ConcurrentHashMap<String, Disposable>()

    /**
     * Replaces the marks of every scenario in [results] (keyed by scenario header line) with those
     * from its new [RunResult]. Resolves steps on the calling background thread (one
     * `documentSymbol` request), then applies the highlighters on the EDT. Never throws.
     */
    fun update(project: Project, uri: String, results: Map<Int, RunResult>) {
        try {
            val needsSymbols = results.values.any { r -> r.rows.any { it.outcome == RunOutcome.FAILED } }
            val symbols = if (needsSymbols) ReqnrollRequestSender.documentSymbol(project, uri) else null
            val marksByScenario = results.mapValues { (line, result) ->
                if (symbols == null) emptyList() else computeMarks(symbols, line, result)
            }
            ApplicationManager.getApplication().invokeLater {
                if (!project.isDisposed) applyOnEdt(project, uri, marksByScenario)
            }
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("FailedStepGutterMarks: could not update marks for $uri", ex)
        }
    }

    /**
     * The failed-step marks for the scenario at [scenarioLine]: one per distinct failed step line
     * (Scenario Outline rows can fail on different steps), each tooltip listing the rows that failed
     * there. Failed rows whose step can't be located are skipped. `internal` for testability.
     */
    internal fun computeMarks(symbols: List<DocumentSymbol>, scenarioLine: Int, result: RunResult): List<FailedStepMark> {
        val located = result.rows
            .filter { it.outcome == RunOutcome.FAILED }
            .mapNotNull { row ->
                FailedStepLocator.locate(symbols, scenarioLine, row.failedStepIndex, row.failedStepText)
                    ?.let { line -> line to row }
            }
        return located
            .groupBy({ it.first }, { it.second })
            .map { (line, rows) -> FailedStepMark(line, renderTooltip(rows)) }
            .sortedBy { it.line }
    }

    /** Tooltip text: the traced failing step, then each failed row with the first line of its error. `internal` for testability. */
    internal fun renderTooltip(rows: List<RunResultRow>): String {
        val header = "Failed - ${rows.firstNotNullOfOrNull { it.failedStepText } ?: "step failed"}"
        val details = rows.mapNotNull { row ->
            val error = row.errorMessage?.lineSequence()?.firstOrNull { it.isNotBlank() }?.trim()
            if (error != null) "${row.displayName}: $error" else null
        }
        return (listOf(header) + details).joinToString("\n")
    }

    private fun applyOnEdt(project: Project, uri: String, marksByScenario: Map<Int, List<FailedStepMark>>) {
        val filePath = lspUriToLocalPath(uri) ?: return
        val editors = EditorFactory.getInstance().allEditors.filter { editor ->
            editor.project == project && isEditorFor(editor, filePath)
        }

        for ((scenarioLine, marks) in marksByScenario) {
            clear(uri, scenarioLine)
            if (marks.isEmpty() || editors.isEmpty()) continue
            val added = mutableListOf<Applied>()
            for (editor in editors) {
                val document = editor.document
                val markupModel = editor.markupModel as? MarkupModelEx ?: continue
                for (mark in marks) {
                    if (mark.line < 0 || mark.line >= document.lineCount) continue
                    val highlighter = markupModel.addLineHighlighter(null, mark.line, HighlighterLayer.ADDITIONAL_SYNTAX)
                    highlighter.gutterIconRenderer = FailedStepGutterIconRenderer(mark.tooltip)
                    added.add(Applied(markupModel, highlighter))
                }
            }
            if (added.isNotEmpty()) applied[uri to scenarioLine] = added
        }
        if (applied.keys.any { it.first == uri }) ensureEditListener(uri, editors.map { it.document }.distinct())
    }

    private fun isEditorFor(editor: Editor, filePath: String): Boolean {
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return false
        return FileUtil.pathsEqual(file.path, filePath)
    }

    /** Removes the marks for one scenario (EDT). */
    private fun clear(uri: String, scenarioLine: Int) {
        applied.remove(uri to scenarioLine)?.forEach {
            // An editor closed since the mark was added has already disposed its markup model.
            try {
                it.markupModel.removeHighlighter(it.highlighter)
            } catch (ex: Exception) {
                ReqnrollDebugLogger.warn("FailedStepGutterMarks: could not remove a stale mark", ex)
            }
        }
    }

    /** Removes every mark for [uri] and its edit listener (EDT). */
    private fun clearAll(uri: String) {
        for (key in applied.keys.filter { it.first == uri }) clear(uri, key.second)
        editListeners.remove(uri)?.let { Disposer.dispose(it) }
    }

    private fun ensureEditListener(uri: String, documents: List<Document>) {
        if (editListeners.containsKey(uri)) return
        val disposable = Disposer.newDisposable("Reqnroll failed-step marks: $uri")
        editListeners[uri] = disposable
        val listener = object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                ApplicationManager.getApplication().invokeLater { clearAll(uri) }
            }
        }
        for (document in documents) document.addDocumentListener(listener, disposable)
    }
}

/** Gutter icon for a failed step, with the failure text as its hover tooltip. Equality is by tooltip so the platform can de-duplicate identical renderers. */
private class FailedStepGutterIconRenderer(private val tooltip: String) : GutterIconRenderer() {
    override fun getIcon(): Icon = AllIcons.RunConfigurations.TestState.Red2
    override fun getTooltipText(): String = tooltip
    override fun getAlignment(): Alignment = Alignment.LEFT
    override fun equals(other: Any?): Boolean = other is FailedStepGutterIconRenderer && other.tooltip == tooltip
    override fun hashCode(): Int = tooltip.hashCode()
}
