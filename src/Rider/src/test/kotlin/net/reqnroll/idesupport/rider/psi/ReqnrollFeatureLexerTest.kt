package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollFeatureLexerTest {
    private fun lex(
        text: String,
        start: Int = 0,
        end: Int = text.length,
        language: String = "en",
    ): List<Pair<IElementType, String>> {
        val lexer = ReqnrollFeatureLexer(language)
        lexer.start(text, start, end, 0)
        val tokens = ArrayList<Pair<IElementType, String>>()
        while (lexer.tokenType != null) {
            tokens.add(lexer.tokenType!! to text.substring(lexer.tokenStart, lexer.tokenEnd))
            lexer.advance()
        }
        return tokens
    }

    private fun kind(kind: GherkinTokenKind) = ReqnrollFeatureTokenTypes.of(kind)

    @Test
    fun `a step line lexes into whitespace, keyword and text`() {
        val tokens = lex("Feature: f\n  Scenario: s\n    Given x\n")

        assertEquals(
            listOf(
                kind(GherkinTokenKind.KEYWORD) to "Feature:",
                TokenType.WHITE_SPACE to " ",
                kind(GherkinTokenKind.NAME) to "f",
                TokenType.WHITE_SPACE to "\n  ",
                kind(GherkinTokenKind.KEYWORD) to "Scenario:",
                TokenType.WHITE_SPACE to " ",
                kind(GherkinTokenKind.NAME) to "s",
                TokenType.WHITE_SPACE to "\n    ",
                kind(GherkinTokenKind.STEP_KEYWORD) to "Given",
                TokenType.WHITE_SPACE to " ",
                kind(GherkinTokenKind.STEP_TEXT) to "x",
                TokenType.WHITE_SPACE to "\n",
            ),
            tokens,
        )
    }

    @Test
    fun `a line the grammar skips becomes one unmatched token`() {
        val tokens = lex("Feature: f\n  Scenario: s\n    Given x\n  Bogus line here\n    Given y\n")

        assertEquals(
            listOf(ReqnrollFeatureTokenTypes.UNMATCHED to "Bogus line here"),
            tokens.filter { it.first === ReqnrollFeatureTokenTypes.UNMATCHED },
        )
    }

    @Test
    fun `lexing a sub-range returns the tokens inside it, clipped to its bounds`() {
        val text = "Feature: feature name\n"

        val tokens = lex(text, start = 9, end = 13)

        assertEquals(listOf(kind(GherkinTokenKind.NAME) to "feat"), tokens)
    }

    @Test
    fun `restarting from a later token boundary gives the same tail`() {
        val text = "Feature: f\n  Scenario: s\n    Given x\n"
        val whole = lex(text)
        val boundary = text.indexOf("    Given")

        val tail = lex(text, start = boundary)

        // The first token of the tail is the whitespace clipped to the restart point; the rest match exactly.
        assertEquals(whole.takeLast(tail.size - 1), tail.drop(1))
        assertEquals(TokenType.WHITE_SPACE to "    ", tail.first())
    }

    @Test
    fun `the default language is used for keywords`() {
        val tokens = lex("Fonctionnalité: f\n  Scénario: s\n    Soit x\n", language = "fr")

        assertEquals(kind(GherkinTokenKind.STEP_KEYWORD) to "Soit", tokens.first { it.second == "Soit" })
    }

    @Test
    fun `empty text has no tokens`() {
        assertEquals(emptyList(), lex(""))
    }

    @Test
    fun `the lexer state is always zero`() {
        val lexer = ReqnrollFeatureLexer()
        lexer.start("Feature: f\n", 0, 11, 0)
        while (lexer.tokenType != null) {
            assertEquals(0, lexer.state)
            lexer.advance()
        }
    }
}
