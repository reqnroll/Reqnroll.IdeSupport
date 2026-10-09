package net.reqnroll.idesupport.rider.actions

import com.intellij.openapi.actionSystem.ActionPlaces
import net.reqnroll.idesupport.rider.lsp.protocol.FindHookLocation
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class GoToHooksRunnerTest {
    @Test
    fun `renderLabel includes hook type, method name, file name, and 1-based line`() {
        val item = FindHookLocation(
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
        val item = FindHookLocation(
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

    // Issue #861: the entry point reported as the closed Source enum shared with VS and VS Code.

    // The property bag the runner transmits per entry point, pinned to the literals shared with
    // VS and VS Code: the action maps its event place, the lens passes CodeLens.
    @Test
    fun `transmitted property bag per entry point carries the cross-IDE Source literal`() {
        val fromContextMenu = GoToHooksRunner.telemetryProperties(GoToHooksRunner.sourceForPlace(ActionPlaces.EDITOR_POPUP))
        val fromShortcut = GoToHooksRunner.telemetryProperties(GoToHooksRunner.sourceForPlace(ActionPlaces.KEYBOARD_SHORTCUT))
        val fromLens = GoToHooksRunner.telemetryProperties(RiderTelemetryTransmitter.GO_TO_HOOK_SOURCE_CODE_LENS)

        assertEquals(mapOf("Source" to "ContextMenu"), fromContextMenu)
        assertEquals(mapOf("Source" to "Command"), fromShortcut)
        assertEquals(mapOf("Source" to "CodeLens"), fromLens)
    }

    @Test
    fun `sourceForPlace maps the editor popup to ContextMenu and any other place to Command`() {
        assertEquals("ContextMenu", GoToHooksRunner.sourceForPlace(ActionPlaces.EDITOR_POPUP))
        assertEquals("Command", GoToHooksRunner.sourceForPlace(ActionPlaces.KEYBOARD_SHORTCUT))
        assertEquals("Command", GoToHooksRunner.sourceForPlace(ActionPlaces.ACTION_SEARCH))
    }

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
