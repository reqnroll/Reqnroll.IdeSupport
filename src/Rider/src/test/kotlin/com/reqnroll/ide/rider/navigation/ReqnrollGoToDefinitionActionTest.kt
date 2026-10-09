package com.reqnroll.ide.rider.navigation

import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import kotlin.test.Test
import kotlin.test.assertSame

class ReqnrollGoToDefinitionActionTest {
    class Plain : AnAction() {
        override fun actionPerformed(e: AnActionEvent) = Unit
    }

    class Wrapper(private val inner: AnAction) : AnAction() {
        override fun actionPerformed(e: AnActionEvent) = Unit

        @Suppress("unused")
        fun getFrontendAction(): AnAction = inner
    }

    @Test
    fun `unwraps an action exposing getFrontendAction`() {
        val inner = Plain()
        assertSame(inner, ReqnrollGoToDefinitionAction.frontendActionOf(Wrapper(inner)))
    }

    @Test
    fun `returns an unwrapped action unchanged`() {
        val plain = Plain()
        assertSame(plain, ReqnrollGoToDefinitionAction.frontendActionOf(plain))
    }
}
