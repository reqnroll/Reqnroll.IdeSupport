package net.reqnroll.idesupport.rider.psi

import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class FeatureSyntaxTest {
    private fun tokens(text: String, language: String = "en") =
        FeatureSyntax.parse(text, language).filterIsInstance<SyntaxEvent.Token>()
            .map { it.kind to text.substring(it.start, it.end) }

    @Test
    fun `step line splits into keyword, text and placeholder`() {
        val tokens = tokens("Feature: f\n  Scenario Outline: s\n    Given I have <n> things\n")

        assertEquals(
            listOf(
                GherkinTokenKind.KEYWORD to "Feature:",
                GherkinTokenKind.NAME to "f",
                GherkinTokenKind.KEYWORD to "Scenario Outline:",
                GherkinTokenKind.NAME to "s",
                GherkinTokenKind.STEP_KEYWORD to "Given",
                GherkinTokenKind.STEP_TEXT to "I have ",
                GherkinTokenKind.PLACEHOLDER to "<n>",
                GherkinTokenKind.STEP_TEXT to " things",
            ),
            tokens,
        )
    }

    @Test
    fun `tags, comment and table cells are reported separately`() {
        val tokens = tokens("@a @b\nFeature: f\n  # note\n  Scenario: s\n    Given x\n      | k | v \\| w |\n      | 1 | 2 |\n")

        assertEquals(
            listOf(GherkinTokenKind.TAG to "@a", GherkinTokenKind.TAG to "@b"),
            tokens.take(2),
        )
        assertTrue(GherkinTokenKind.COMMENT to "# note" in tokens)
        assertEquals(
            listOf(
                GherkinTokenKind.PIPE to "|", GherkinTokenKind.HEADER_CELL to "k",
                GherkinTokenKind.PIPE to "|", GherkinTokenKind.HEADER_CELL to "v \\| w",
                GherkinTokenKind.PIPE to "|",
                GherkinTokenKind.PIPE to "|", GherkinTokenKind.CELL to "1",
                GherkinTokenKind.PIPE to "|", GherkinTokenKind.CELL to "2",
                GherkinTokenKind.PIPE to "|",
            ),
            tokens.takeLast(10),
        )
    }

    @Test
    fun `each table starts again with a header row`() {
        val tokens = tokens(
            "Feature: f\n  Scenario Outline: s\n    Given x\n      | a |\n      | b |\n" +
                "    Examples:\n      | n |\n      | 1 |\n",
        )

        val cells = tokens.filter { it.first == GherkinTokenKind.HEADER_CELL || it.first == GherkinTokenKind.CELL }
        assertEquals(
            listOf(
                GherkinTokenKind.HEADER_CELL to "a", GherkinTokenKind.CELL to "b",
                GherkinTokenKind.HEADER_CELL to "n", GherkinTokenKind.CELL to "1",
            ),
            cells,
        )
    }

    @Test
    fun `doc string keeps its content type and content`() {
        val tokens = tokens("Feature: f\n  Scenario: s\n    Given x\n      \"\"\"json\n      {}\n      \"\"\"\n")

        assertEquals(
            listOf(
                GherkinTokenKind.DOC_STRING_DELIMITER to "\"\"\"",
                GherkinTokenKind.DOC_STRING_TYPE to "json",
                GherkinTokenKind.DOC_STRING_CONTENT to "{}",
                GherkinTokenKind.DOC_STRING_DELIMITER to "\"\"\"",
            ),
            tokens.takeLast(4),
        )
    }

    @Test
    fun `localised keywords are recognised from the language header`() {
        val tokens = tokens("# language: fr\nFonctionnalité: f\n  Scénario: s\n    Soit x\n")

        assertEquals(GherkinTokenKind.LANGUAGE_HEADER to "# language: fr", tokens.first())
        assertTrue(GherkinTokenKind.STEP_KEYWORD to "Soit" in tokens)
    }

    @Test
    fun `the default language applies when there is no header`() {
        val tokens = tokens("Fonctionnalité: f\n  Scénario: s\n    Soit x\n", language = "fr")

        assertTrue(GherkinTokenKind.STEP_KEYWORD to "Soit" in tokens)
    }

    @Test
    fun `an unknown default language falls back to English`() {
        val tokens = tokens("Feature: f\n  Scenario: s\n    Given x\n", language = "not-a-language")

        assertTrue(GherkinTokenKind.STEP_KEYWORD to "Given" in tokens)
    }

    @Test
    fun `an unknown language header is treated as a comment and the file still parses`() {
        val tokens = tokens("# language: xx\nFeature: f\n  Scenario: s\n    Given x\n")

        assertEquals(GherkinTokenKind.COMMENT to "# language: xx", tokens.first())
        assertTrue(GherkinTokenKind.STEP_KEYWORD to "Given" in tokens)
    }

    @Test
    fun `an unterminated doc string still yields balanced rules`() {
        val events = FeatureSyntax.parse("Feature: f\n  Scenario: s\n    Given x\n      \"\"\"\n      open\n")

        assertBalanced(events)
        assertTrue(events.any { it is SyntaxEvent.RuleStart && it.rule == "DocString" })
    }

    @Test
    fun `CRLF line endings give the same token texts`() {
        val lf = tokens("Feature: f\n  Scenario: s\n    Given x\n")
        val crlf = tokens("Feature: f\r\n  Scenario: s\r\n    Given x\r\n")

        assertEquals(lf, crlf)
    }

    @Test
    fun `empty and whitespace-only text produce no events`() {
        assertEquals(emptyList(), FeatureSyntax.parse(""))
        assertEquals(emptyList(), tokens("   \n\n"))
    }

    private fun assertBalanced(events: List<SyntaxEvent>) {
        val open = ArrayDeque<String>()
        for (event in events) {
            when (event) {
                is SyntaxEvent.RuleStart -> open.addLast(event.rule)
                is SyntaxEvent.RuleEnd -> assertEquals(open.removeLastOrNull(), event.rule)
                is SyntaxEvent.Token -> Unit
            }
        }
        assertTrue(open.isEmpty(), "rules left open: $open")
    }
}
