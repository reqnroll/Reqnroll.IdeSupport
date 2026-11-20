package net.reqnroll.idesupport.rider.psi

import io.cucumber.gherkin.GherkinSyntaxWalker
import net.reqnroll.idesupport.rider.gherkin.GherkinSyntaxSink
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/** One step of walking a feature file: a grammar rule opening or closing, or a token. */
internal sealed interface SyntaxEvent {
    data class RuleStart(val rule: String) : SyntaxEvent
    data class RuleEnd(val rule: String) : SyntaxEvent
    data class Token(val kind: GherkinTokenKind, val start: Int, val end: Int) : SyntaxEvent
}

/**
 * The structure and tokens of a feature file as the Cucumber Gherkin parser sees them. This is the single place
 * the lexer and the tree parser both read from, so the two cannot disagree about where a token starts.
 */
internal object FeatureSyntax {
    /** The dialect used when a file has no `# language:` header and no project default is known. */
    const val FALLBACK_LANGUAGE = "en"

    /**
     * Walks [text] and returns the events in document order. Never throws: syntax errors do not stop the walk,
     * and rules left open by an error are closed, so rule starts and ends always balance.
     */
    fun parse(text: CharSequence, defaultLanguage: String = FALLBACK_LANGUAGE): List<SyntaxEvent> {
        val events = ArrayList<SyntaxEvent>()
        GherkinSyntaxWalker.walk(text.toString(), defaultLanguage, object : GherkinSyntaxSink {
            override fun startRule(rule: String) {
                events.add(SyntaxEvent.RuleStart(rule))
            }

            override fun endRule(rule: String) {
                events.add(SyntaxEvent.RuleEnd(rule))
            }

            override fun token(kind: GherkinTokenKind, start: Int, end: Int) {
                events.add(SyntaxEvent.Token(kind, start, end))
            }
        })
        return events
    }
}
