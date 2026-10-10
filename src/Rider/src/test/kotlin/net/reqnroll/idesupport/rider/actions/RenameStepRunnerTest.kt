package net.reqnroll.idesupport.rider.actions

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RenameStepRunnerTest {
    @Test
    fun `isValidNewExpression accepts a non-blank change`() {
        assertTrue(RenameStepRunner.isValidNewExpression("the first number is {int}", "the number entered is {int}"))
    }

    @Test
    fun `isValidNewExpression rejects null (dialog cancelled)`() {
        assertFalse(RenameStepRunner.isValidNewExpression("the first number is {int}", null))
    }

    @Test
    fun `isValidNewExpression rejects blank input`() {
        assertFalse(RenameStepRunner.isValidNewExpression("the first number is {int}", "   "))
    }

    @Test
    fun `isValidNewExpression rejects an unchanged expression`() {
        assertFalse(RenameStepRunner.isValidNewExpression("the first number is {int}", "the first number is {int}"))
    }
}
