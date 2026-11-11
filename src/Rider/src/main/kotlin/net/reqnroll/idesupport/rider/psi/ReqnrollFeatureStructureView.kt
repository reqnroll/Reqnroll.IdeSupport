package net.reqnroll.idesupport.rider.psi

import com.intellij.ide.structureView.StructureViewBuilder
import com.intellij.ide.structureView.StructureViewModel
import com.intellij.ide.structureView.StructureViewModelBase
import com.intellij.ide.structureView.StructureViewTreeElement
import com.intellij.ide.structureView.TreeBasedStructureViewBuilder
import com.intellij.ide.structureView.impl.common.PsiTreeElementBase
import com.intellij.ide.util.treeView.smartTree.Sorter
import com.intellij.lang.ASTNode
import com.intellij.lang.PsiStructureViewFactory
import com.intellij.openapi.editor.Editor
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.tree.IElementType
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/**
 * The Structure view of a feature file: Feature, then its Background, Rules and Scenarios, then their steps and
 * Examples, built from the tree. Registered for the Gherkin language, so it takes precedence over the platform's
 * generic language-server outline and gives an outline on every supported Rider version, including those whose
 * generic client has none.
 */
class ReqnrollFeatureStructureViewFactory : PsiStructureViewFactory {
    override fun getStructureViewBuilder(psiFile: PsiFile): StructureViewBuilder =
        object : TreeBasedStructureViewBuilder() {
            override fun createStructureViewModel(editor: Editor?): StructureViewModel =
                ReqnrollFeatureStructureViewModel(psiFile, editor)

            override fun isRootNodeShown(): Boolean = false
        }
}

private class ReqnrollFeatureStructureViewModel(file: PsiFile, editor: Editor?) :
    StructureViewModelBase(file, editor, ReqnrollFeatureStructureElement(file)) {
    init {
        withSuitableClasses(PsiElement::class.java)
    }

    // Document order is the order that matters in a feature file, so no alphabetical sorter is offered.
    override fun getSorters(): Array<Sorter> = emptyArray()
}

/** One node of the Structure view: the file, or a block whose line names it. */
internal class ReqnrollFeatureStructureElement(element: PsiElement) : PsiTreeElementBase<PsiElement>(element) {

    override fun getChildrenBase(): Collection<StructureViewTreeElement> {
        val node = element?.node ?: return emptyList()
        return ReqnrollFeatureStructure.childBlocks(node).map { ReqnrollFeatureStructureElement(it.psi) }
    }

    override fun getPresentableText(): String? {
        val psi = element ?: return null
        if (psi is PsiFile) return psi.name
        return ReqnrollFeatureStructure.headerText(psi.node)
    }
}

internal object ReqnrollFeatureStructure {
    /** The blocks shown in the Structure view. Wrappers such as ScenarioDefinition are looked through. */
    val blocks: Set<IElementType> =
        listOf("Feature", "Rule", "Background", "Scenario", "Examples", "Step")
            .map(ReqnrollFeatureElementTypes::forRule).toSet()

    private val keywordTokens = setOf(
        ReqnrollFeatureTokenTypes.of(GherkinTokenKind.KEYWORD),
        ReqnrollFeatureTokenTypes.of(GherkinTokenKind.STEP_KEYWORD),
    )

    /** The blocks directly under [node], looking through wrappers such as ScenarioDefinition. */
    fun childBlocks(node: ASTNode): List<ASTNode> {
        val result = ArrayList<ASTNode>()
        var child = node.firstChildNode
        while (child != null) {
            if (child.elementType in blocks) result.add(child) else result.addAll(childBlocks(child))
            child = child.treeNext
        }
        return result
    }

    /** The block's naming line from its keyword to the end of the line, such as `Scenario: Eating` or `Given x`. */
    fun headerText(node: ASTNode): String? {
        val keyword = firstKeyword(node) ?: return null
        val text = node.psi.containingFile?.text ?: return null
        val start = keyword.startOffset
        var end = start
        while (end < text.length && text[end] != '\n' && text[end] != '\r') end++
        return text.substring(start, end).trim()
    }

    private fun firstKeyword(node: ASTNode): ASTNode? {
        if (node.elementType in keywordTokens) return node
        var child = node.firstChildNode
        while (child != null) {
            firstKeyword(child)?.let { return it }
            child = child.treeNext
        }
        return null
    }
}
