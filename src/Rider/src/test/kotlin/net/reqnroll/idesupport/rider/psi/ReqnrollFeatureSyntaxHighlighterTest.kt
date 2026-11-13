package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.TokenType
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind
import net.reqnroll.idesupport.rider.lsp.semantictokens.ReqnrollSemanticTokenTypes
import net.reqnroll.idesupport.rider.lsp.semantictokens.ReqnrollSemanticTokensSupport
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ReqnrollFeatureSyntaxHighlighterTest {
    private val highlighter = ReqnrollFeatureSyntaxHighlighter()

    /** Token kinds drawn in the plain text colour: names, step text, pipes, and the unmatched remainder. */
    private val plain = setOf(GherkinTokenKind.NAME, GherkinTokenKind.STEP_TEXT, GherkinTokenKind.PIPE)

    @Test
    fun `every token kind is either coloured or deliberately plain`() {
        val unaccounted = GherkinTokenKind.entries.filter {
            it !in ReqnrollFeatureSyntaxHighlighter.attributesByKind && it !in plain
        }

        assertEquals(emptyList(), unaccounted)
    }

    @Test
    fun `a keyword token gets the keyword colour`() {
        val highlights = highlighter.getTokenHighlights(ReqnrollFeatureTokenTypes.of(GherkinTokenKind.STEP_KEYWORD))

        assertEquals(listOf(ReqnrollFeatureTextAttributes.KEYWORD), highlights.toList())
    }

    @Test
    fun `plain and whitespace tokens have no colour`() {
        assertTrue(highlighter.getTokenHighlights(ReqnrollFeatureTokenTypes.of(GherkinTokenKind.STEP_TEXT)).isEmpty())
        assertTrue(highlighter.getTokenHighlights(TokenType.WHITE_SPACE).isEmpty())
        assertTrue(highlighter.getTokenHighlights(ReqnrollFeatureTokenTypes.UNMATCHED).isEmpty())
        assertTrue(highlighter.getTokenHighlights(null).isEmpty())
    }

    @Test
    fun `header cells and body cells are coloured differently`() {
        val header = highlighter.getTokenHighlights(ReqnrollFeatureTokenTypes.of(GherkinTokenKind.HEADER_CELL)).single()
        val body = highlighter.getTokenHighlights(ReqnrollFeatureTokenTypes.of(GherkinTokenKind.CELL)).single()

        assertEquals(ReqnrollFeatureTextAttributes.DATA_TABLE_HEADER, header)
        assertEquals(ReqnrollFeatureTextAttributes.DATA_TABLE, body)
    }

    @Test
    fun `the server's semantic tokens use the same colours for the kinds the lexer colours`() {
        val semantic = ReqnrollSemanticTokensSupport()
        val sameColours = mapOf(
            GherkinTokenKind.KEYWORD to ReqnrollSemanticTokenTypes.KEYWORD,
            GherkinTokenKind.STEP_KEYWORD to ReqnrollSemanticTokenTypes.KEYWORD,
            GherkinTokenKind.TAG to ReqnrollSemanticTokenTypes.TAG,
            GherkinTokenKind.COMMENT to ReqnrollSemanticTokenTypes.COMMENT,
            GherkinTokenKind.DESCRIPTION to ReqnrollSemanticTokenTypes.DESCRIPTION,
            GherkinTokenKind.DOC_STRING_CONTENT to ReqnrollSemanticTokenTypes.DOC_STRING,
            GherkinTokenKind.CELL to ReqnrollSemanticTokenTypes.DATA_TABLE,
            GherkinTokenKind.HEADER_CELL to ReqnrollSemanticTokenTypes.DATA_TABLE_HEADER,
            GherkinTokenKind.PLACEHOLDER to ReqnrollSemanticTokenTypes.SCENARIO_OUTLINE_PLACEHOLDER,
        )

        for ((kind, tokenType) in sameColours) {
            assertEquals(
                semantic.getTextAttributesKey(tokenType, emptyList()),
                ReqnrollFeatureSyntaxHighlighter.attributesByKind[kind],
                "$kind should match $tokenType",
            )
        }
    }

    @Test
    fun `the highlighting lexer is the feature lexer with the given default language`() {
        val lexer = ReqnrollFeatureSyntaxHighlighter("fr").highlightingLexer
        val text = "Fonctionnalité: f\n  Scénario: s\n    Soit x\n"
        lexer.start(text)

        val types = ArrayList<Any?>()
        while (lexer.tokenType != null) {
            types.add(lexer.tokenType)
            lexer.advance()
        }

        assertTrue(ReqnrollFeatureTokenTypes.of(GherkinTokenKind.STEP_KEYWORD) in types)
    }
}
