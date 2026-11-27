package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.TokenType
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Checks our own code against hard inputs: the lexer must cover every character with exactly one token, and the
 * syntax walker must close every rule it opens, on valid files and on files the parser rejects. This does not
 * re-test Gherkin's parsing; the cucumber/gherkin project does that with the same files.
 *
 * The inputs (`src/test/resources/gherkin-corpus`) are a selection from that project's test files at the release
 * matching the pinned library version, kept to the edge cases these checks depend on: incomplete documents,
 * doc strings, tables and escaped pipes, descriptions with comments, tabs, `# language:` (emoji, spaces, invalid),
 * and files that end mid-construct or are not Gherkin at all.
 */
class GherkinCorpusTest {
    private fun corpus(kind: String): List<File> {
        val dir = File(javaClass.getResource("/gherkin-corpus/$kind")!!.toURI())
        return dir.listFiles { file -> file.extension == "feature" }!!.sortedBy { it.name }
    }

    private fun lex(text: String): List<Triple<Any?, Int, Int>> {
        val lexer = ReqnrollFeatureLexer()
        lexer.start(text, 0, text.length, 0)
        val tokens = ArrayList<Triple<Any?, Int, Int>>()
        while (lexer.tokenType != null) {
            tokens.add(Triple(lexer.tokenType, lexer.tokenStart, lexer.tokenEnd))
            lexer.advance()
        }
        return tokens
    }

    private fun assertCoversWithoutOverlap(name: String, text: String) {
        var position = 0
        for ((_, start, end) in lex(text)) {
            assertEquals(position, start, "$name: gap or overlap before offset $start")
            assertTrue(end > start, "$name: empty token at $start")
            position = end
        }
        assertEquals(text.length, position, "$name: tokens stop before the end of the text")
    }

    @Test
    fun `the corpus is present`() {
        assertTrue(corpus("good").size >= 14)
        assertTrue(corpus("bad").size >= 6)
    }

    @Test
    fun `valid files are covered by tokens with nothing left unmatched`() {
        for (file in corpus("good")) {
            val text = file.readText()
            assertCoversWithoutOverlap(file.name, text)
            val unmatched = lex(text).filter { it.first === ReqnrollFeatureTokenTypes.UNMATCHED }
            assertTrue(unmatched.isEmpty(), "${file.name}: unmatched text at ${unmatched.map { it.second }}")
        }
    }

    @Test
    fun `invalid files are still fully covered by tokens`() {
        for (file in corpus("bad")) {
            assertCoversWithoutOverlap(file.name, file.readText())
        }
    }

    @Test
    fun `rules are balanced for every file`() {
        for (file in corpus("good") + corpus("bad")) {
            val open = ArrayDeque<String>()
            for (event in FeatureSyntax.parse(file.readText())) {
                when (event) {
                    is SyntaxEvent.RuleStart -> open.addLast(event.rule)
                    is SyntaxEvent.RuleEnd -> assertEquals(open.removeLastOrNull(), event.rule, file.name)
                    is SyntaxEvent.Token -> Unit
                }
            }
            assertTrue(open.isEmpty(), "${file.name}: rules left open $open")
        }
    }

    @Test
    fun `whitespace is the only token type besides the Gherkin ones in a valid file`() {
        val text = corpus("good").first { it.name == "background.feature" }.readText()

        val types = lex(text).map { it.first }.toSet()

        assertTrue(TokenType.WHITE_SPACE in types)
        assertTrue(ReqnrollFeatureTokenTypes.UNMATCHED !in types)
    }

    @Test
    fun `every grammar rule the library can report has a known element type`() {
        val ruleTypeClass = Class.forName("io.cucumber.gherkin.Parser\$RuleType")
        val reported = ruleTypeClass.enumConstants.map { (it as Enum<*>).name }
            .filter { !it.startsWith("_") && it != "None" && it != "GherkinDocument" }

        val unknown = reported - ReqnrollFeatureElementTypes.knownRules.toSet()

        assertTrue(unknown.isEmpty(), "grammar rules without an element type: $unknown")
    }
}
