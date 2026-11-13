package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.PsiElement
import com.intellij.spellchecker.tokenizer.SpellcheckingStrategy
import com.intellij.spellchecker.tokenizer.Tokenizer
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/**
 * Spell-checks only the prose of a feature file: comments, names, descriptions and step text. Keywords, tags,
 * placeholders, table cells, doc-string content and the language header are identifiers or data, so they are
 * left alone instead of producing a squiggle on every `Scenario` in a non-English file.
 */
class ReqnrollFeatureSpellcheckingStrategy : SpellcheckingStrategy() {
    override fun getTokenizer(element: PsiElement?): Tokenizer<*> {
        val type = element?.node?.elementType
        return if (type in proseTokens) TEXT_TOKENIZER else EMPTY_TOKENIZER
    }

    private companion object {
        val proseTokens = setOf(
            ReqnrollFeatureTokenTypes.of(GherkinTokenKind.COMMENT),
            ReqnrollFeatureTokenTypes.of(GherkinTokenKind.NAME),
            ReqnrollFeatureTokenTypes.of(GherkinTokenKind.DESCRIPTION),
            ReqnrollFeatureTokenTypes.of(GherkinTokenKind.STEP_TEXT),
        )
    }
}
