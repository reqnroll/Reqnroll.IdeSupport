package net.reqnroll.idesupport.rider.psi

import com.intellij.lang.ASTNode
import com.intellij.lang.PsiBuilder
import com.intellij.lang.PsiParser
import com.intellij.psi.tree.IElementType

/**
 * Builds the tree of a feature file by replaying the structure the Cucumber Gherkin parser reports
 * ([FeatureSyntax]) onto a [PsiBuilder] whose lexer is a [ReqnrollFeatureLexer].
 *
 * The tree always covers the whole text: tokens the grammar skipped while recovering from an error are consumed
 * into whichever rule is open, and anything left at the end belongs to the file. No syntax error is ever
 * reported here; diagnostics come from the language server.
 */
class ReqnrollFeatureParser(
    private val defaultLanguage: String = FeatureSyntax.FALLBACK_LANGUAGE,
) : PsiParser {
    override fun parse(root: IElementType, builder: PsiBuilder): ASTNode {
        val file = builder.mark()
        replay(builder, FeatureSyntax.parse(builder.originalText, defaultLanguage))
        while (!builder.eof()) builder.advanceLexer()
        file.done(root)
        return builder.treeBuilt
    }

    private class OpenRule(val rule: String, var marker: PsiBuilder.Marker?)

    private fun replay(builder: PsiBuilder, events: List<SyntaxEvent>) {
        val open = ArrayList<OpenRule>()

        fun advanceTo(offset: Int) {
            while (!builder.eof() && builder.currentOffset < offset) builder.advanceLexer()
        }

        fun materialisePendingStarts() {
            for (rule in open) {
                if (rule.marker == null) rule.marker = builder.mark()
            }
        }

        for (event in events) {
            when (event) {
                is SyntaxEvent.RuleStart -> open.add(OpenRule(event.rule, null))
                is SyntaxEvent.Token -> {
                    advanceTo(event.start)
                    materialisePendingStarts()
                    advanceTo(event.end)
                }
                is SyntaxEvent.RuleEnd -> {
                    val rule = open.removeLastOrNull() ?: continue
                    rule.marker?.done(ReqnrollFeatureElementTypes.forRule(rule.rule))
                }
            }
        }
        while (open.isNotEmpty()) {
            val rule = open.removeLast()
            rule.marker?.done(ReqnrollFeatureElementTypes.forRule(rule.rule))
        }
    }
}
