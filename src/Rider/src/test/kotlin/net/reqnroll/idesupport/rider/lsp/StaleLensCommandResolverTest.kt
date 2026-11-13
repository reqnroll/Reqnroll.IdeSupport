package net.reqnroll.idesupport.rider.lsp

import com.google.gson.JsonPrimitive
import net.reqnroll.idesupport.rider.lsp.StaleLensCommandResolver.Resolution
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.Command
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import kotlin.test.Test
import kotlin.test.assertEquals

class StaleLensCommandResolverTest {
    private val uri = "file:///c:/r/PriceCalculation.feature"

    private fun ownLevel(title: String, line: Int) = Command(title, "reqnroll.goToHooks", listOf(uri, line, 0, true))
    private fun stepHooks(title: String, line: Int) = Command(title, "reqnroll.goToHooks", listOf(uri, line, 0, true, true))
    private fun lens(command: Command, line: Int) = CodeLens(Range(Position(line, 0), Position(line, 0)), command, null)

    // The lenses of a feature with a Feature-level hook and two scenarios, each with scenario and step hooks:
    // Feature: on line 1, scenarios on lines 14 and 20, their first steps on lines 15 and 21.
    private val fresh = listOf(
        lens(ownLevel("1 hook", 1), 1),
        lens(ownLevel("2 hooks", 14), 14),
        lens(stepHooks("1 step hook", 15), 14),
        lens(ownLevel("2 hooks", 20), 20),
        lens(stepHooks("1 step hook", 21), 20),
    )

    @Test
    fun `a command present in the fresh response is current`() {
        assertEquals(Resolution.Current, StaleLensCommandResolver.resolve(stepHooks("1 step hook", 15), fresh))
    }

    @Test
    fun `gson primitives compare equal to the plain values of the fresh response`() {
        val clicked = Command(
            "1 step hook", "reqnroll.goToHooks",
            listOf(JsonPrimitive(uri), JsonPrimitive(15), JsonPrimitive(0), JsonPrimitive(true), JsonPrimitive(true)),
        )
        assertEquals(Resolution.Current, StaleLensCommandResolver.resolve(clicked, fresh))
    }

    @Test
    fun `a step hooks lens one line stale resolves to the step hooks lens, not the scenario line`() {
        // Computed before a line was added above the feature: it points at line 14, now the Scenario: line.
        assertEquals(
            Resolution.Rebased(stepHooks("1 step hook", 15)),
            StaleLensCommandResolver.resolve(stepHooks("1 step hook", 14), fresh),
        )
        assertEquals(
            Resolution.Rebased(stepHooks("1 step hook", 21)),
            StaleLensCommandResolver.resolve(stepHooks("1 step hook", 20), fresh),
        )
    }

    @Test
    fun `an own-level lens one line stale resolves to the own-level lens of the same block`() {
        assertEquals(Resolution.Rebased(ownLevel("1 hook", 1)), StaleLensCommandResolver.resolve(ownLevel("1 hook", 0), fresh))
        assertEquals(Resolution.Rebased(ownLevel("2 hooks", 20)), StaleLensCommandResolver.resolve(ownLevel("2 hooks", 19), fresh))
    }

    @Test
    fun `a lens that is several lines stale still finds its block`() {
        // Three lines removed above the second scenario since the lens was computed.
        assertEquals(
            Resolution.Rebased(stepHooks("1 step hook", 21)),
            StaleLensCommandResolver.resolve(stepHooks("1 step hook", 24), fresh),
        )
    }

    @Test
    fun `the same title wins when two lenses of the kind are equally near`() {
        val fresh = listOf(lens(ownLevel("1 hook", 10), 10), lens(ownLevel("3 hooks", 14), 14))
        assertEquals(Resolution.Rebased(ownLevel("3 hooks", 14)), StaleLensCommandResolver.resolve(ownLevel("3 hooks", 12), fresh))
    }

    @Test
    fun `a lens kind with no fresh lens is gone`() {
        val ownLevelOnly = fresh.filter { it.command.arguments.size == 4 }
        assertEquals(Resolution.Gone, StaleLensCommandResolver.resolve(stepHooks("1 step hook", 14), ownLevelOnly))
        assertEquals(Resolution.Gone, StaleLensCommandResolver.resolve(stepHooks("1 step hook", 14), emptyList()))
    }

    @Test
    fun `other lens commands are matched by their own name`() {
        val usages = Command("2 step usages", "reqnroll.findStepUsages", listOf("file:///c:/r/Steps.cs", 12, 4))
        val fresh = listOf(
            lens(Command("2 step usages", "reqnroll.findStepUsages", listOf("file:///c:/r/Steps.cs", 13, 4)), 13),
            lens(Command("1 scenario", "reqnroll.goToMatchingScenarios", listOf("file:///c:/r/Steps.cs", 12, 4)), 12),
        )
        assertEquals(
            Resolution.Rebased(Command("2 step usages", "reqnroll.findStepUsages", listOf("file:///c:/r/Steps.cs", 13, 4))),
            StaleLensCommandResolver.resolve(usages, fresh),
        )
    }
}
