package net.reqnroll.idesupport.rider.testrunner

import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ScenarioTestTargetItem
import net.reqnroll.idesupport.rider.lsp.protocol.TestOutcomeRowItem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class GutterRunOutcomesTest {
    private val assembly = """C:\repo\Specs\bin\Debug\net8.0\Specs.dll"""

    private fun scenario(line: Int, vararg methods: String) =
        ScenarioRunTarget(line, methods.map { ScenarioTestTargetItem(declaringTypeFullName = "Specs.CalculatorFeature", methodName = it) })

    private fun failed(step: Int) = GetTestOutcomeResponse(
        found = true,
        aggregate = "Failed",
        rows = listOf(TestOutcomeRowItem(displayName = "Add", outcome = "Failed", failedStepIndex = step, failedStepText = "Then the result is 3")),
    )

    private val passed = GetTestOutcomeResponse(found = true, aggregate = "Passed", rows = listOf(TestOutcomeRowItem(displayName = "Add", outcome = "Passed")))

    @Test
    fun `a scenario seen for the first time is applied`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()

        val changed = GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> failed(2) }

        assertEquals(setOf(4), changed.keys)
        assertEquals(RunOutcome.FAILED, changed.getValue(4).outcome)
        assertEquals(2, changed.getValue(4).rows.single().failedStepIndex)
    }

    @Test
    fun `an unchanged outcome is not applied again`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> failed(2) }

        val changed = GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> failed(2) }

        assertTrue(changed.isEmpty())
    }

    @Test
    fun `a changed outcome is applied`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> failed(2) }

        val changed = GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> passed }

        assertEquals(RunOutcome.PASSED, changed.getValue(4).outcome)
    }

    @Test
    fun `only the scenario whose outcome changed is applied`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        val scenarios = listOf(scenario(4, "Add"), scenario(9, "Subtract"))
        GutterRunOutcomes.changedResults(assembly, scenarios, seen) { _, _ -> passed }

        val changed = GutterRunOutcomes.changedResults(assembly, scenarios, seen) { _, method ->
            if (method == "Subtract") failed(1) else passed
        }

        assertEquals(setOf(9), changed.keys)
    }

    @Test
    fun `a scenario with a missing, stale or running method is skipped and not recorded`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        val unusable = listOf(
            null,
            GetTestOutcomeResponse(found = false),
            passed.copy(isStale = true),
            passed.copy(isRunning = true),
        )

        for (response in unusable) {
            val changed = GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add", "AddMore")), seen) { _, method ->
                if (method == "Add") passed else response
            }
            assertTrue(changed.isEmpty(), "skipped for $response")
        }
        assertTrue(seen.isEmpty())
    }

    @Test
    fun `methods of another assembly are tracked apart`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> passed }

        val changed = GutterRunOutcomes.changedResults("""C:\repo\Other\bin\Other.dll""", listOf(scenario(4, "Add")), seen) { _, _ -> passed }

        assertEquals(setOf(4), changed.keys)
    }

    @Test
    fun `assembly paths differing only in case or separators are the same method`() {
        val seen = mutableMapOf<String, GetTestOutcomeResponse>()
        GutterRunOutcomes.changedResults(assembly, listOf(scenario(4, "Add")), seen) { _, _ -> passed }

        val changed = GutterRunOutcomes.changedResults(assembly.uppercase().replace('\\', '/'), listOf(scenario(4, "Add")), seen) { _, _ -> passed }

        assertTrue(changed.isEmpty())
    }
}
