package net.reqnroll.idesupport.rider.testrunner

import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ScenarioTestTargetItem
import net.reqnroll.idesupport.rider.lsp.protocol.TestOutcomeRowItem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class RunOutcomeCollectorTest {
    // ── combineServerOutcomes ────────────────────────────────────────────────

    private fun outcome(aggregate: String, vararg rows: TestOutcomeRowItem) =
        GetTestOutcomeResponse(found = true, aggregate = aggregate, rows = rows.toList())

    private fun row(displayName: String, outcome: String, failedStepText: String? = null) =
        TestOutcomeRowItem(displayName = displayName, outcome = outcome, failedStepText = failedStepText)

    @Test
    fun `combineServerOutcomes carries the failed-step index and error message for the gutter mark`() {
        val failing = TestOutcomeRowItem(
            displayName = "r", outcome = "Failed", failedStepText = "When x", failedStepIndex = 2, errorMessage = "boom")
        val result = RunOutcomeCollector.combineServerOutcomes(listOf(outcome("Failed", failing)))
        assertEquals(RunResultRow("r", RunOutcome.FAILED, "When x", 2, "boom"), result.rows[0])
    }

    @Test
    fun `combineServerOutcomes is Passed when every method's aggregate passed`() {
        val result = RunOutcomeCollector.combineServerOutcomes(listOf(outcome("Passed", row("r1", "Passed"))))
        assertEquals(RunOutcome.PASSED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes is Failed if any method's aggregate failed, even when others passed`() {
        val result = RunOutcomeCollector.combineServerOutcomes(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes flattens rows across every method and carries the failed-step text`() {
        val result = RunOutcomeCollector.combineServerOutcomes(
            listOf(
                outcome("Passed", row("row 1", "Passed")),
                outcome("Failed", row("row 2", "Failed", "When the calculation explodes")),
            ),
        )
        assertEquals(2, result.rows.size)
        assertEquals(RunResultRow("row 1", RunOutcome.PASSED, null), result.rows[0])
        assertEquals(RunResultRow("row 2", RunOutcome.FAILED, "When the calculation explodes"), result.rows[1])
    }

    // ── scenarioResults ──────────────────────────────────────────────────────

    private fun target(method: String) =
        ScenarioTestTargetItem(declaringTypeFullName = "NS.Feat", methodName = method)

    @Test
    fun `scenarioResults gives each scenario only its own methods' outcome`() {
        val scenarios = listOf(ScenarioRunTarget(3, listOf(target("A"))), ScenarioRunTarget(7, listOf(target("B"))))
        val outcomes = mapOf(
            ("NS.Feat" to "A") to outcome("Passed", row("a", "Passed")),
            ("NS.Feat" to "B") to outcome("Failed", row("b", "Failed")),
        )

        val results = RunOutcomeCollector.scenarioResults(scenarios, outcomes, RunResult(RunOutcome.FAILED))

        assertEquals(RunOutcome.PASSED, results[3]?.outcome)
        assertEquals(RunOutcome.FAILED, results[7]?.outcome)
    }

    @Test
    fun `scenarioResults skips a scenario whose method has no outcome`() {
        val scenarios = listOf(ScenarioRunTarget(3, listOf(target("A"))))
        val outcomes = mapOf(("NS.Feat" to "Other") to outcome("Passed", row("o", "Passed")))

        assertEquals(emptyMap<Int, RunResult>(), RunOutcomeCollector.scenarioResults(scenarios, outcomes, RunResult(RunOutcome.PASSED)))
    }

    @Test
    fun `scenarioResults without server outcomes marks every scenario passed only when the aggregate passed`() {
        val scenarios = listOf(ScenarioRunTarget(3, listOf(target("A"))), ScenarioRunTarget(7, listOf(target("B"))))

        val passed = RunOutcomeCollector.scenarioResults(scenarios, null, RunResult(RunOutcome.PASSED))
        assertEquals(setOf(3, 7), passed.keys)
        assertEquals(emptyMap<Int, RunResult>(), RunOutcomeCollector.scenarioResults(scenarios, null, RunResult(RunOutcome.FAILED)))
    }

    // ── trxResult ────────────────────────────────────────────────────────────

    private fun trxRow(outcome: String) = TrxUnitTestResult("t", outcome, "", null)

    @Test
    fun `trxResult is not Passed when the TRX has no result rows`() {
        // An empty (or unparseable) TRX carries no signal, so it must never render as a green pass.
        assertEquals(RunOutcome.INCONCLUSIVE, RunOutcomeCollector.trxResult(emptyList()).outcome)
    }

    @Test
    fun `trxResult is Passed when every row passed and Failed when any row failed`() {
        assertEquals(RunOutcome.PASSED, RunOutcomeCollector.trxResult(listOf(trxRow("Passed"), trxRow("Passed"))).outcome)
        assertEquals(RunOutcome.FAILED, RunOutcomeCollector.trxResult(listOf(trxRow("Passed"), trxRow("Failed"))).outcome)
    }

    // ── combineIfComplete ────────────────────────────────────────────────────

    @Test
    fun `combineIfComplete combines when every method has a fresh, settled outcome`() {
        val result = RunOutcomeCollector.combineIfComplete(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result?.outcome)
        assertEquals(2, result?.rows?.size)
    }

    @Test
    fun `combineIfComplete is null when any method's lookup failed or was not found`() {
        assertNull(RunOutcomeCollector.combineIfComplete(listOf(outcome("Passed", row("r1", "Passed")), null)))
        assertNull(
            RunOutcomeCollector.combineIfComplete(
                listOf(outcome("Passed", row("r1", "Passed")), GetTestOutcomeResponse(found = false)),
            ),
        )
    }

    @Test
    fun `combineIfComplete rejects a previous run's stale outcome instead of reporting it as this run's`() {
        val stale = outcome("Failed", row("r1", "Failed")).copy(isStale = true)
        assertNull(RunOutcomeCollector.combineIfComplete(listOf(stale)))
    }

    @Test
    fun `combineIfComplete keeps waiting while a method is still marked running`() {
        val running = outcome("Running", row("r1", "Running")).copy(isRunning = true)
        assertNull(RunOutcomeCollector.combineIfComplete(listOf(outcome("Passed", row("r0", "Passed")), running)))
    }

    @Test
    fun `combineIfComplete is null for no methods at all`() {
        assertNull(RunOutcomeCollector.combineIfComplete(emptyList()))
    }

    // ── exitCodeResult ───────────────────────────────────────────────────────

    @Test
    fun `exitCodeResult is Passed only for exit code 0`() {
        assertEquals(RunOutcome.PASSED, RunOutcomeCollector.exitCodeResult(0).outcome)
        assertEquals(RunOutcome.FAILED, RunOutcomeCollector.exitCodeResult(1).outcome)
        assertEquals(RunOutcome.FAILED, RunOutcomeCollector.exitCodeResult(5).outcome)
    }
}
