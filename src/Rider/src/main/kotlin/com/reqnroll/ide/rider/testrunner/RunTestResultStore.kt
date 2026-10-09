package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.util.Disposer
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.localPathToLspUri
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

/** Outcome of the last `dotnet test` run for one scenario. */
enum class RunOutcome { PASSED, FAILED }

/**
 * Last-run result for one scenario, keyed by (file URI, 0-based scenario header line).
 *
 * [rows] carries per-row detail sourced from the LSP server's `TestOutcomeStore` (#700/#702) when
 * the run went through that pipeline — one entry per Scenario Outline example row, each with its
 * own failed-step text when available. Empty for a result that fell back to plain TRX parsing
 * (the pre-#700 path, kept as a fallback for one release — see [RunTestRunner]), which only ever
 * knew the coarse pass/fail bit.
 */
data class RunResult(val outcome: RunOutcome, val rows: List<RunResultRow> = emptyList())

/** One row (test case) of a [RunResult] — mirrors the shape of the server's `TestOutcomeRowDto`, trimmed to what the CodeVision tooltip renders. */
data class RunResultRow(
    val displayName: String,
    val outcome: RunOutcome,
    val failedStepText: String? = null,
    /** 0-based execution index of the failing step across the traced steps (Background first), for [FailedStepLocator]. */
    val failedStepIndex: Int? = null,
    val errorMessage: String? = null,
)

/** One scenario inside a Feature/Rule "Run Scenarios" run: its own lens line and resolved targets, so the run can record a per-scenario [RunResult] as well as the container's. */
data class ScenarioRunTarget(val startLine: Int, val targets: List<ScenarioTestTargetItem>)

/**
 * In-memory, per-scenario last-run result — tracked entirely in the plugin's own state (design doc
 * §5's "own execution" decision, mirrored from VS Code's `testResultStore.ts`; issue #262). Not
 * persisted across IDE restarts.
 *
 * Results are keyed by line, which describes the file as it was run, so a file's results are
 * dropped on the first edit to its document (#985) — otherwise lines inserted above a scenario
 * would leave another scenario showing the old glyph/tooltip. This mirrors [FailedStepGutterMarks],
 * and also bounds the store to the files run since their last edit.
 */
object RunTestResultStore {
    private val results = ConcurrentHashMap<Pair<String, Int>, RunResult>()
    private val editHookInstalled = AtomicBoolean(false)

    fun set(uri: String, startLine: Int, result: RunResult) {
        results[uri to startLine] = result
        ensureEditHook()
    }

    fun get(uri: String, startLine: Int): RunResult? = results[uri to startLine]

    /** Drops every result recorded for [uri]. */
    fun clearFile(uri: String) {
        results.keys.removeIf { it.first == uri }
    }

    /** Installs, once, an application-wide document listener that clears a file's results on edit. No-op without a running application (unit tests). */
    private fun ensureEditHook() {
        if (ApplicationManager.getApplication() == null || !editHookInstalled.compareAndSet(false, true)) return
        try {
            val disposable = Disposer.newDisposable("Reqnroll run results edit hook")
            EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
                override fun documentChanged(event: DocumentEvent) {
                    if (results.isEmpty()) return
                    val file = FileDocumentManager.getInstance().getFile(event.document) ?: return
                    clearFile(localPathToLspUri(file.path))
                }
            }, disposable)
        } catch (ex: Exception) {
            editHookInstalled.set(false)
            ReqnrollDebugLogger.warn("RunTestResultStore: could not install the edit hook", ex)
        }
    }
}
