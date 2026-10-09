package com.reqnroll.ide.rider.lsp

import com.google.gson.JsonPrimitive
import org.eclipse.lsp4j.Command
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ReqnrollLspCommandsSupportTest {
    private fun parse(name: String, vararg args: Any) =
        ReqnrollLspCommandsSupport.parseLensCommand(Command("title", name, args.toList()))

    @Test
    fun `parses a step usages lens`() {
        assertEquals(
            LensCommand.FindStepUsages("file:///c:/r/Steps.cs", 14, 8),
            parse("reqnroll.findStepUsages", "file:///c:/r/Steps.cs", 14, 8),
        )
    }

    @Test
    fun `accepts gson primitives as the platform delivers them`() {
        assertEquals(
            LensCommand.GoToHooks("file:///c:/r/F.feature", 3, 4, true),
            parse(
                "reqnroll.goToHooks",
                JsonPrimitive("file:///c:/r/F.feature"), JsonPrimitive(3), JsonPrimitive(4), JsonPrimitive(true),
            ),
        )
    }

    @Test
    fun `hook lens without an ownLevelOnly argument defaults to false`() {
        assertEquals(
            LensCommand.GoToHooks("file:///r/F.feature", 1, 2, false),
            parse("reqnroll.goToHooks", "file:///r/F.feature", 1, 2),
        )
    }

    @Test
    fun `parses matching scenarios and no usages`() {
        assertEquals(
            LensCommand.GoToMatchingScenarios("file:///r/Hooks.cs", 9, 12),
            parse("reqnroll.goToMatchingScenarios", "file:///r/Hooks.cs", 9, 12),
        )
        assertEquals(LensCommand.NoStepUsages, ReqnrollLspCommandsSupport.parseLensCommand(Command("0 step usages", "reqnroll.noStepUsages")))
    }

    @Test
    fun `ignores other commands and malformed arguments`() {
        assertNull(parse("reqnroll.toggleComment", "file:///r/F.feature", 1, 2))
        assertNull(parse("reqnroll.findStepUsages"))
        assertNull(parse("reqnroll.goToHooks", "file:///r/F.feature", 1))
    }
}
