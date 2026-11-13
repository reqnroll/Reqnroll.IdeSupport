package net.reqnroll.idesupport.rider.debugger

import net.reqnroll.idesupport.rider.debugger.StepIntoStepDefinitionAction.LocationChoice
import org.eclipse.lsp4j.Location
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import kotlin.test.Test
import kotlin.test.assertEquals

class StepIntoStepDefinitionActionTest {
    private fun location(uri: String, line: Int, character: Int) =
        Location(uri, Range(Position(line, character), Position(line, character + 5)))

    @Test
    fun `no locations means no step definition`() {
        assertEquals(LocationChoice.None, StepIntoStepDefinitionAction.chooseLocation(emptyList()))
    }

    @Test
    fun `one location is the target`() {
        val only = location("file:///c:/r/Steps.cs", 12, 16)
        assertEquals(LocationChoice.Single(only), StepIntoStepDefinitionAction.chooseLocation(listOf(only)))
    }

    @Test
    fun `the same method reported twice is one target`() {
        val first = location("file:///c:/r/Steps.cs", 12, 16)
        val again = location("file:///c:/r/Steps.cs", 12, 16)
        assertEquals(LocationChoice.Single(first), StepIntoStepDefinitionAction.chooseLocation(listOf(first, again)))
    }

    @Test
    fun `an ambiguous step has several targets`() {
        val locations = listOf(location("file:///c:/r/Steps.cs", 12, 16), location("file:///c:/r/Other.cs", 4, 16))
        assertEquals(LocationChoice.Several(2), StepIntoStepDefinitionAction.chooseLocation(locations))
    }

    @Test
    fun `the lookup character is the step's first non-blank character`() {
        val text = "Scenario: x\n    And the client added 1 pcs\n"
        val lineStart = text.indexOf("    And")
        val lineEnd = text.indexOf('\n', lineStart)
        assertEquals(4, StepIntoStepDefinitionAction.firstNonBlankCharacter(text, lineStart, lineEnd))
        assertEquals(0, StepIntoStepDefinitionAction.firstNonBlankCharacter("\t\t", 0, 0))
        assertEquals(2, StepIntoStepDefinitionAction.firstNonBlankCharacter("\t\t", 0, 2))
    }
}
