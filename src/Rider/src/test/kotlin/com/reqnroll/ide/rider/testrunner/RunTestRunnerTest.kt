package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.protocol.GetTestOutcomeResponse
import com.reqnroll.ide.rider.lsp.protocol.RegisterTestRunResponse
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import com.reqnroll.ide.rider.lsp.protocol.TestOutcomeRowItem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class RunTestRunnerTest {
    private fun target(
        declaringTypeFullName: String = "Tests.FFeature",
        methodName: String = "AddNumbers",
    ) = ScenarioTestTargetItem(declaringTypeFullName = declaringTypeFullName, methodName = methodName)

    // ── findOwningProjectPath ────────────────────────────────────────────────

    @Test
    fun `finds the project whose folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj", "/repo/Bar/Bar.csproj")
        assertEquals("/repo/Foo/Foo.csproj", RunTestRunner.findOwningProjectPath("/repo/Foo/Features/A.feature", projects))
    }

    @Test
    fun `deepest matching folder wins for a nested project`() {
        val projects = listOf("/repo/Foo.csproj", "/repo/Foo/Nested/Nested.csproj")
        assertEquals(
            "/repo/Foo/Nested/Nested.csproj",
            RunTestRunner.findOwningProjectPath("/repo/Foo/Nested/A.feature", projects),
        )
    }

    @Test
    fun `returns null when no project folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj")
        assertNull(RunTestRunner.findOwningProjectPath("/elsewhere/A.feature", projects))
    }

    @Test
    fun `returns null for an empty project list`() {
        assertNull(RunTestRunner.findOwningProjectPath("/repo/Foo/A.feature", emptyList()))
    }

    // ── buildTestFilter ──────────────────────────────────────────────────────

    @Test
    fun `a single target produces one filter term`() {
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(listOf(target())))
    }

    @Test
    fun `row-tests targets sharing one method collapse to a single term`() {
        val targets = listOf(target(), target(), target())
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(targets))
    }

    @Test
    fun `individual-methods targets with distinct method names each get their own term`() {
        val targets = listOf(
            target(methodName = "CheckValue_1"),
            target(methodName = "CheckValue_2"),
        )
        assertEquals(
            "FullyQualifiedName=Tests.FFeature.CheckValue_1|FullyQualifiedName=Tests.FFeature.CheckValue_2",
            RunTestRunner.buildTestFilter(targets),
        )
    }

    @Test
    fun `an empty target list produces an empty filter`() {
        assertEquals("", RunTestRunner.buildTestFilter(emptyList()))
    }

    // ── buildLoggerArgument ──────────────────────────────────────────────────

    @Test
    fun `buildLoggerArgument matches the friendly name and parameter keys VS injects into runsettings`() {
        val registration = RegisterTestRunResponse(success = true, runId = "run-1", endpoint = "127.0.0.1:5000")

        assertEquals(
            "ReqnrollIde;Endpoint=127.0.0.1:5000;RunId=run-1;IdeProcessId=4242",
            RunTestRunner.buildLoggerArgument(registration, ideProcessId = 4242L),
        )
    }

    // ── combineServerOutcomes ────────────────────────────────────────────────

    private fun outcome(aggregate: String, vararg rows: TestOutcomeRowItem) =
        GetTestOutcomeResponse(found = true, aggregate = aggregate, rows = rows.toList())

    private fun row(displayName: String, outcome: String, failedStepText: String? = null) =
        TestOutcomeRowItem(displayName = displayName, outcome = outcome, failedStepText = failedStepText)

    @Test
    fun `combineServerOutcomes is Passed when every method's aggregate passed`() {
        val result = RunTestRunner.combineServerOutcomes(listOf(outcome("Passed", row("r1", "Passed"))))
        assertEquals(RunOutcome.PASSED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes is Failed if any method's aggregate failed, even when others passed`() {
        val result = RunTestRunner.combineServerOutcomes(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes flattens rows across every method and carries the failed-step text`() {
        val result = RunTestRunner.combineServerOutcomes(
            listOf(
                outcome("Passed", row("row 1", "Passed")),
                outcome("Failed", row("row 2", "Failed", "When the calculation explodes")),
            ),
        )
        assertEquals(2, result.rows.size)
        assertEquals(RunResultRow("row 1", RunOutcome.PASSED, null), result.rows[0])
        assertEquals(RunResultRow("row 2", RunOutcome.FAILED, "When the calculation explodes"), result.rows[1])
    }

    // ── combineIfComplete ────────────────────────────────────────────────────

    @Test
    fun `combineIfComplete combines when every method has a fresh, settled outcome`() {
        val result = RunTestRunner.combineIfComplete(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result?.outcome)
        assertEquals(2, result?.rows?.size)
    }

    @Test
    fun `combineIfComplete is null when any method's lookup failed or was not found`() {
        assertNull(RunTestRunner.combineIfComplete(listOf(outcome("Passed", row("r1", "Passed")), null)))
        assertNull(
            RunTestRunner.combineIfComplete(
                listOf(outcome("Passed", row("r1", "Passed")), GetTestOutcomeResponse(found = false)),
            ),
        )
    }

    @Test
    fun `combineIfComplete rejects a previous run's stale outcome instead of reporting it as this run's`() {
        val stale = outcome("Failed", row("r1", "Failed")).copy(isStale = true)
        assertNull(RunTestRunner.combineIfComplete(listOf(stale)))
    }

    @Test
    fun `combineIfComplete keeps waiting while a method is still marked running`() {
        val running = outcome("Running", row("r1", "Running")).copy(isRunning = true)
        assertNull(RunTestRunner.combineIfComplete(listOf(outcome("Passed", row("r0", "Passed")), running)))
    }

    @Test
    fun `combineIfComplete is null for no methods at all`() {
        assertNull(RunTestRunner.combineIfComplete(emptyList()))
    }
}
