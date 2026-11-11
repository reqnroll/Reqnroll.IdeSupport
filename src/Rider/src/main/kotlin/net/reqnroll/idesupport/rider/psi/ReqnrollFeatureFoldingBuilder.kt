package net.reqnroll.idesupport.rider.psi

import com.intellij.lang.ASTNode
import com.intellij.lang.folding.FoldingBuilderEx
import com.intellij.lang.folding.FoldingDescriptor
import com.intellij.openapi.editor.Document
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiElement
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/**
 * Folds a feature file from its tree: a Feature, Rule, Background, Scenario or Examples block, and a doc string
 * or table, each collapsing from the end of its first line to the end of its content, so the line that names it
 * stays visible next to the `...` placeholder. A Feature or Rule folds from the end of its keyword line, not from
 * a tag line above it.
 *
 * Computed locally from the PSI, so it needs no language-server round trip and is there as soon as the file opens.
 */
class ReqnrollFeatureFoldingBuilder : FoldingBuilderEx(), DumbAware {
    override fun buildFoldRegions(root: PsiElement, document: Document, quick: Boolean): Array<FoldingDescriptor> =
        foldRegions(root.node, document.charsSequence)
            .map { FoldingDescriptor(it.node, it.range) }
            .toTypedArray()

    override fun getPlaceholderText(node: ASTNode): String = PLACEHOLDER

    override fun isCollapsedByDefault(node: ASTNode): Boolean = false

    internal class Region(val node: ASTNode, val range: TextRange)

    internal companion object {
        const val PLACEHOLDER = "..."

        private val foldableRules: Set<com.intellij.psi.tree.IElementType> = listOf(
            "Feature", "Rule", "Background", "Scenario", "Examples", "DocString", "DataTable", "ExamplesTable",
        ).map(ReqnrollFeatureElementTypes::forRule).toSet()

        private val keyword = ReqnrollFeatureTokenTypes.of(GherkinTokenKind.KEYWORD)

        /** The foldable regions under [root], outer blocks before the blocks inside them. */
        fun foldRegions(root: ASTNode, text: CharSequence): List<Region> {
            val regions = ArrayList<Region>()
            collect(root, text, regions)
            return regions
        }

        private fun collect(node: ASTNode, text: CharSequence, into: MutableList<Region>) {
            if (node.elementType in foldableRules) {
                val start = endOfLineAt(headerOffset(node), text)
                val end = node.textRange.endOffset
                if (end > start) into.add(Region(node, TextRange(start, end)))
            }
            var child = node.firstChildNode
            while (child != null) {
                collect(child, text, into)
                child = child.treeNext
            }
        }

        /** The offset of the line that names a block: its own keyword, else its first character. */
        private fun headerOffset(node: ASTNode): Int = firstKeyword(node)?.startOffset ?: node.startOffset

        private fun firstKeyword(node: ASTNode): ASTNode? {
            if (node.elementType == keyword) return node
            var child = node.firstChildNode
            while (child != null) {
                firstKeyword(child)?.let { return it }
                child = child.treeNext
            }
            return null
        }

        private fun endOfLineAt(offset: Int, text: CharSequence): Int {
            var i = offset
            while (i < text.length && text[i] != '\n' && text[i] != '\r') i++
            return i
        }
    }
}
