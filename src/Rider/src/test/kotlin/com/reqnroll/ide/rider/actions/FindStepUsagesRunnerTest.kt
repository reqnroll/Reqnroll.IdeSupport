package com.reqnroll.ide.rider.actions

import com.reqnroll.ide.rider.lsp.protocol.FindStepUsageItem
import kotlin.test.Test
import kotlin.test.assertEquals

class FindStepUsagesRunnerTest {
    @Test
    fun `renderLabel includes keyword, step text, scenario, and project when all present`() {
        val item = FindStepUsageItem(
            uri = "file:///repo/Calculator.feature",
            startLine = 6,
            startChar = 4,
            endLine = 6,
            endChar = 30,
            stepText = "the first number is 50",
            keyword = "Given",
            scenarioName = "Add two numbers",
            projectName = "Calculator",
        )

        assertEquals(
            "Given the first number is 50 — Add two numbers [Calculator]",
            FindStepUsagesRunner.renderLabel(item),
        )
    }

    @Test
    fun `renderLabel falls back to the file name when stepText is null`() {
        val item = FindStepUsageItem(uri = "file:///repo/features/Calculator.feature")

        assertEquals("Calculator.feature", FindStepUsagesRunner.renderLabel(item))
    }

    @Test
    fun `renderLabel omits optional segments that are null`() {
        val item = FindStepUsageItem(uri = "file:///repo/Calculator.feature", stepText = "the first number is 50")

        assertEquals("the first number is 50", FindStepUsagesRunner.renderLabel(item))
    }

    @Test
    fun `renderLabel joins featureName and ruleName when both are set`() {
        val item = FindStepUsageItem(
            uri = "file:///repo/Calculator.feature",
            stepText = "the first number is 50",
            featureName = "Calculator",
            ruleName = "Basic arithmetic",
        )

        assertEquals(
            "the first number is 50 (Calculator / Basic arithmetic)",
            FindStepUsagesRunner.renderLabel(item),
        )
    }
}
