package com.reqnroll.ide.rider.actions

import com.reqnroll.ide.rider.lsp.protocol.GoToHookLocation
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class GoToHooksRunnerTest {
    @Test
    fun `renderLabel includes hook type, method name, file name, and 1-based line`() {
        val item = GoToHookLocation(
            uri = "file:///repo/Hooks.cs",
            startLine = 9,
            startChar = 4,
            hookType = "BeforeScenario",
            hookOrder = 10000,
            methodName = "SetUpDatabase",
        )

        assertEquals(
            "[BeforeScenario] SetUpDatabase (Hooks.cs:10)",
            GoToHooksRunner.renderLabel(item),
        )
    }

    @Test
    fun `renderLabel falls back gracefully when uri has no path segments`() {
        val item = GoToHookLocation(
            uri = "Hooks.cs",
            startLine = 0,
            hookType = "AfterStep",
            methodName = "TearDown",
        )

        assertEquals(
            "[AfterStep] TearDown (Hooks.cs:1)",
            GoToHooksRunner.renderLabel(item),
        )
    }

    // Regression tests for issue #372 follow-up: clicking a hook-count CodeVision lens should
    // always show the picker, even for a single match, so the user can see which hook it refers
    // to rather than being jumped straight there. The manual "Go to Hooks" action keeps the
    // direct-navigate shortcut for a single match.

    @Test
    fun `shouldNavigateDirectly is true for a single hook when alwaysShowPicker is not set`() {
        assertTrue(GoToHooksRunner.shouldNavigateDirectly(hookCount = 1, alwaysShowPicker = false))
    }

    @Test
    fun `shouldNavigateDirectly is false for a single hook when alwaysShowPicker is set`() {
        assertFalse(GoToHooksRunner.shouldNavigateDirectly(hookCount = 1, alwaysShowPicker = true))
    }

    @Test
    fun `shouldNavigateDirectly is false for multiple hooks regardless of alwaysShowPicker`() {
        assertFalse(GoToHooksRunner.shouldNavigateDirectly(hookCount = 2, alwaysShowPicker = false))
        assertFalse(GoToHooksRunner.shouldNavigateDirectly(hookCount = 2, alwaysShowPicker = true))
    }
}
