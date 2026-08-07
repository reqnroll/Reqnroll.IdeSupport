package com.reqnroll.ide.rider.testrunner

import java.util.concurrent.ConcurrentHashMap

/** Outcome of the last `dotnet test` run for one scenario. */
enum class RunOutcome { PASSED, FAILED }

/** Last-run result for one scenario, keyed by (file URI, 0-based scenario header line). */
data class RunResult(val outcome: RunOutcome)

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
