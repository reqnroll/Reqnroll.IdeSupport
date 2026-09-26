package com.reqnroll.ide.rider.actions

import com.reqnroll.ide.rider.lsp.protocol.UnusedStepDefinitionItem
import kotlin.test.Test
import kotlin.test.assertEquals

class FindUnusedStepDefinitionsActionTest {
    @Test
    fun `renderLabel includes class, method, attribute, and project when all present`() {
        val item = UnusedStepDefinitionItem(
            projectName = "Calculator",
            className = "CalculatorSteps",
            methodName = "GivenIHaveEnteredNumber",
            bindingExpression = "I have entered {int}",
            stepDefinitionType = "Given",
            sourceFile = "/repo/CalculatorSteps.cs",
            sourceLine = 12,
            sourceChar = 4,
        )

        assertEquals(
            "CalculatorSteps.GivenIHaveEnteredNumber - [Given(\"I have entered {int}\")] [Calculator]",
            FindUnusedStepDefinitionsAction.renderLabel(item),
        )
    }

    @Test
    fun `renderLabel omits optional segments that are null`() {
        val item = UnusedStepDefinitionItem(
            projectName = null,
            className = "CalculatorSteps",
            methodName = "GivenIHaveEnteredNumber",
            bindingExpression = null,
            sourceFile = "/repo/CalculatorSteps.cs",
            sourceLine = 12,
            sourceChar = 4,
        )

        assertEquals(
            "CalculatorSteps.GivenIHaveEnteredNumber",
            FindUnusedStepDefinitionsAction.renderLabel(item),
        )
    }

    @Test
    fun `renderLabel falls back to just the method name when className is null`() {
        val item = UnusedStepDefinitionItem(methodName = "GivenIHaveEnteredNumber")

        assertEquals(
            "GivenIHaveEnteredNumber",
            FindUnusedStepDefinitionsAction.renderLabel(item),
        )
    }

    // ── Unresolvable source paths (issue #540) ──────────────────────────────────

    @Test
    fun `renderLabel marks an entry whose source is not on this machine`() {
        // Such a row cannot be navigated to, so it must not look identical to every other row and
        // then do nothing when chosen.
        val item = UnusedStepDefinitionItem(
            projectName = "Calculator",
            className = "CalculatorSteps",
            methodName = "GivenIHaveEnteredNumber",
            bindingExpression = "I have entered {int}",
            sourceFile = null,
            sourceLine = 12,
            sourceChar = 4,
            isResolved = false,
            recordedSourceFile = "/workspaces/host-solution/Specs/CalculatorSteps.cs",
        )

        assertEquals(
            "CalculatorSteps.GivenIHaveEnteredNumber - \"I have entered {int}\" [Calculator]" +
                " (source not on this machine)",
            FindUnusedStepDefinitionsAction.renderLabel(item),
        )
    }

    @Test
    fun `renderLabel leaves a resolved entry unmarked`() {
        // Also covers back-compat: a response from a server predating issue #540 omits isResolved,
        // and the data class default keeps those rows rendering exactly as before.
        val item = UnusedStepDefinitionItem(
            className = "CalculatorSteps",
            methodName = "GivenIHaveEnteredNumber",
            sourceFile = "/repo/CalculatorSteps.cs",
        )

        assertEquals(
            "CalculatorSteps.GivenIHaveEnteredNumber",
            FindUnusedStepDefinitionsAction.renderLabel(item),
        )
    }

    // ── Binding attribute (issue #757) ──────────────────────────────────────────

    @Test
    fun `renderAttribute shows the attribute with its expression`() {
        val item = UnusedStepDefinitionItem(bindingExpression = "the sum is {int}", stepDefinitionType = "Then")

        assertEquals("[Then(\"the sum is {int}\")]", FindUnusedStepDefinitionsAction.renderAttribute(item))
    }

    @Test
    fun `renderAttribute shows the bare attribute for a method-name-style binding`() {
        val item = UnusedStepDefinitionItem(stepDefinitionType = "When")

        assertEquals("[When]", FindUnusedStepDefinitionsAction.renderAttribute(item))
    }

    @Test
    fun `renderAttribute quotes the expression alone when the keyword is unknown`() {
        // An older server omits stepDefinitionType.
        val item = UnusedStepDefinitionItem(bindingExpression = "a step")

        assertEquals("\"a step\"", FindUnusedStepDefinitionsAction.renderAttribute(item))
    }

    @Test
    fun `renderAttribute is null when neither keyword nor expression is known`() {
        assertEquals(null, FindUnusedStepDefinitionsAction.renderAttribute(UnusedStepDefinitionItem()))
    }
}
