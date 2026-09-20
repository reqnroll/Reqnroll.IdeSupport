package com.reqnroll.ide.rider.testrunner

import java.util.concurrent.ConcurrentHashMap

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
data class RunResultRow(val displayName: String, val outcome: RunOutcome, val failedStepText: String? = null)

/**
 * In-memory, per-scenario last-run result — tracked entirely in the plugin's own state (design doc
 * §5's "own execution" decision, mirrored from VS Code's `testResultStore.ts`; issue #262). Not
 * persisted across IDE restarts.
 */
object RunTestResultStore {
    private val results = ConcurrentHashMap<Pair<String, Int>, RunResult>()

    fun set(uri: String, startLine: Int, result: RunResult) {
        results[uri to startLine] = result
    }

    fun get(uri: String, startLine: Int): RunResult? = results[uri to startLine]
}
