package com.reqnroll.ide.rider.actions

import com.reqnroll.ide.rider.lsp.protocol.MatchingScenarioLocation
import kotlin.test.Test
import kotlin.test.assertEquals

class GoToMatchingScenariosRunnerTest {
    @Test
    fun `renderLabel includes scenario kind and name for a plain scenario`() {
        val item = MatchingScenarioLocation(
            uri = "file:///repo/Calculator.feature",
            startLine = 4,
            startChar = 0,
            scenarioName = "Add two numbers",
            isOutline = false,
        )

        assertEquals("[Scenario] Add two numbers", GoToMatchingScenariosRunner.renderLabel(item))
    }

    @Test
    fun `renderLabel labels a scenario outline distinctly`() {
        val item = MatchingScenarioLocation(
            uri = "file:///repo/Calculator.feature",
            startLine = 10,
            scenarioName = "Add many numbers",
            isOutline = true,
        )

        assertEquals("[Scenario Outline] Add many numbers", GoToMatchingScenariosRunner.renderLabel(item))
    }

    @Test
    fun `renderLabel falls back gracefully when the scenario has no title`() {
        val item = MatchingScenarioLocation(
            uri = "file:///repo/Calculator.feature",
            startLine = 0,
            scenarioName = "",
            isOutline = false,
        )

        assertEquals("[Scenario] (untitled)", GoToMatchingScenariosRunner.renderLabel(item))
    }

    @Test
    fun `matchingScenariosTitle uses singular wording for exactly one scenario`() {
        assertEquals("1 matching scenario", GoToMatchingScenariosRunner.matchingScenariosTitle(1))
    }

    @Test
    fun `matchingScenariosTitle uses plural wording for more than one scenario`() {
        assertEquals("2 matching scenarios", GoToMatchingScenariosRunner.matchingScenariosTitle(2))
    }
}
