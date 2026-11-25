package net.reqnroll.idesupport.rider.testrunner

import com.intellij.openapi.project.Project
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ScenarioTestTargetItem

/**
 * Turns what a `dotnet test` run reported into [RunResult]s: per-method outcomes from the LSP server
 * (row detail and the failed step), or the TRX or exit-code result when the server has nothing usable.
 *
 * The server pipeline is an enhancement, never the only path. A lookup that comes back empty, stale or
 * incomplete (no server, the logger or reporter failed to load, or the runner exited before the server
 * finished processing) falls back to the result the run itself produced.
 */
internal object RunOutcomeCollector {
    /**
     * How long, and how often, to poll the server for outcomes after `dotnet test` exits. The logger's
     * final `runComplete` write, and the server's processing of it, may not have landed the instant the
     * runner process terminates.
     */
    private const val OUTCOME_POLL_ATTEMPTS = 10
    private const val OUTCOME_POLL_DELAY_MS = 100L

    /**
     * Polls the server for every distinct target method's outcome, retrying briefly
     * ([OUTCOME_POLL_ATTEMPTS] × [OUTCOME_POLL_DELAY_MS]). Returns the responses keyed by (declaring type,
     * method), or null (fall back) unless *every* method has a usable outcome ([usableOrNull]) within the
     * attempts. Blocks the calling thread, so never call it from the EDT.
     */
    fun pollServerOutcomes(
        project: Project,
        assemblyPath: String,
        targets: List<ScenarioTestTargetItem>,
    ): Map<Pair<String, String>, GetTestOutcomeResponse>? {
        val distinctMethods = targets.map { it.declaringTypeFullName to it.methodName }.distinct()
        repeat(OUTCOME_POLL_ATTEMPTS) { attempt ->
            val responses = distinctMethods.map { (type, method) ->
                ReqnrollRequestSender.getTestOutcome(project, assemblyPath, type, method)
            }
            usableOrNull(responses)?.let { usable -> return distinctMethods.zip(usable).toMap() }
            if (attempt < OUTCOME_POLL_ATTEMPTS - 1) Thread.sleep(OUTCOME_POLL_DELAY_MS)
        }
        return null
    }

    /**
     * The per-scenario results of a Feature/Rule run, keyed by each scenario's lens line, so each
     * scenario lens shows its own outcome rather than only the container's. With [serverOutcomes], each
     * scenario combines just its own methods' responses, and a scenario missing any method is skipped.
     * Without them there is no per-method detail: an aggregate [fallback] of PASSED still proves every
     * scenario passed, but a FAILED aggregate does not say which one failed, so none are set.
     */
    fun scenarioResults(
        scenarios: List<ScenarioRunTarget>,
        serverOutcomes: Map<Pair<String, String>, GetTestOutcomeResponse>?,
        fallback: RunResult,
    ): Map<Int, RunResult> {
        val results = mutableMapOf<Int, RunResult>()
        for (scenario in scenarios) {
            if (serverOutcomes == null) {
                if (fallback.outcome == RunOutcome.PASSED) results[scenario.startLine] = RunResult(RunOutcome.PASSED)
                continue
            }
            val methods = scenario.targets.map { it.declaringTypeFullName to it.methodName }.distinct()
            val responses = methods.map { serverOutcomes[it] }
            if (responses.isEmpty() || responses.any { it == null }) continue
            results[scenario.startLine] = combineServerOutcomes(responses.filterNotNull())
        }
        return results
    }

    /**
     * One poll attempt's verdict: [combineServerOutcomes] over [responses] only when every one is usable
     * for *this* run, else null (keep polling, or fall back). See [usableOrNull].
     */
    fun combineIfComplete(responses: List<GetTestOutcomeResponse?>): RunResult? =
        usableOrNull(responses)?.let { combineServerOutcomes(it) }

    /**
     * [responses] unwrapped, only when every one is usable for *this* run: [GetTestOutcomeResponse.found]
     * and neither [GetTestOutcomeResponse.isStale] nor [GetTestOutcomeResponse.isRunning].
     *
     * Pitfall: `found` alone is not enough. Once this run's build rewrites the container, the server
     * returns the *previous* run's outcome with `found = true` and `isStale = true`, so accepting it would
     * show last time's glyph whenever this run's result has not landed yet, or never does. Every method
     * must be usable, not just one, so a multi-target scenario is never summarized from a partial set.
     */
    private fun usableOrNull(responses: List<GetTestOutcomeResponse?>): List<GetTestOutcomeResponse>? {
        if (responses.isEmpty()) return null
        return responses.map { it?.takeIf { r -> r.found && !r.isStale && !r.isRunning } ?: return null }
    }

    /**
     * Combines one [GetTestOutcomeResponse] per distinct target method into one [RunResult]: `Failed` if
     * any method's aggregate failed, the same precedence as the server's `TestOutcomeStore.Aggregate`
     * (`Failed` > `Passed` > everything else).
     */
    fun combineServerOutcomes(outcomes: List<GetTestOutcomeResponse>): RunResult {
        val outcome = if (outcomes.any { it.aggregate == "Failed" }) RunOutcome.FAILED else RunOutcome.PASSED
        val rows = outcomes.flatMap { response ->
            response.rows.map { row ->
                RunResultRow(
                    displayName = row.displayName,
                    outcome = if (row.outcome == "Failed") RunOutcome.FAILED else RunOutcome.PASSED,
                    failedStepText = row.failedStepText,
                    failedStepIndex = row.failedStepIndex,
                    errorMessage = row.errorMessage,
                )
            }
        }
        return RunResult(outcome, rows)
    }

    /**
     * The TRX-only result: one aggregate over every TRX row, with no per-row detail. Zero rows (no
     * `UnitTestResult` entries, or a TRX [TrxParser] could not parse) proves nothing ran or was readable,
     * so it is [RunOutcome.INCONCLUSIVE] rather than a vacuous pass.
     */
    fun trxResult(results: List<TrxUnitTestResult>): RunResult = RunResult(
        when {
            results.isEmpty() -> RunOutcome.INCONCLUSIVE
            results.any { it.outcome == "Failed" } -> RunOutcome.FAILED
            else -> RunOutcome.PASSED
        },
    )

    /** The MTP result when the server has nothing: MTP writes no TRX, so the exit code (0 = all selected tests passed) is the only signal. */
    fun exitCodeResult(exitCode: Int): RunResult =
        RunResult(if (exitCode == 0) RunOutcome.PASSED else RunOutcome.FAILED)
}
