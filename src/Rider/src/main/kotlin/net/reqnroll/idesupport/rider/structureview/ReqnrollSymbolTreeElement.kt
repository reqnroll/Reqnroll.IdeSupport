package net.reqnroll.idesupport.rider.structureview

import com.intellij.icons.AllIcons
import com.intellij.ide.structureView.StructureViewTreeElement
import com.intellij.ide.util.treeView.smartTree.TreeElement
import com.intellij.navigation.ItemPresentation
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.SymbolKind
import javax.swing.Icon

/**
 * Wraps one server-side `DocumentSymbol` (Feature/Rule/Background/Scenario/Step/Examples — see
 * `FeatureDocumentSymbolHandler.ToDocumentSymbol`) as a Structure View node. Deliberately holds no
 * PSI reference: `.feature` files have no `ParserDefinition` (see
 * `ReqnrollFeatureInlayHintsController`'s doc comment), so navigation goes through
 * [OpenFileDescriptor] by offset — the same idiom already used by
 * [net.reqnroll.idesupport.rider.actions.ReqnrollResultPopup.navigateToUri] — rather than a `PsiElement`.
 */
class ReqnrollSymbolTreeElement(
    private val project: Project,
    private val virtualFile: VirtualFile,
    private val symbol: DocumentSymbol,
) : StructureViewTreeElement {
    override fun getValue(): Any = symbol

    override fun getPresentation(): ItemPresentation = object : ItemPresentation {
        override fun getPresentableText(): String = symbol.name
        override fun getLocationString(): String? = symbol.detail
        override fun getIcon(unused: Boolean): Icon = iconFor(symbol.kind)
    }

    override fun getChildren(): Array<TreeElement> =
        symbol.children.orEmpty()
            .map { ReqnrollSymbolTreeElement(project, virtualFile, it) }
            .toTypedArray()

    override fun navigate(requestFocus: Boolean) {
        val start = symbol.selectionRange.start
        OpenFileDescriptor(project, virtualFile, start.line, start.character).navigate(requestFocus)
    }

    override fun canNavigate(): Boolean = true

    override fun canNavigateToSource(): Boolean = true

    companion object {
        /** Mirrors `FeatureDocumentSymbolHandler.ToSymbolKind`'s Feature/Rule/Scenario/Step mapping. `internal` so it's unit-testable without a platform fixture. */
        internal fun iconFor(kind: SymbolKind): Icon = when (kind) {
            SymbolKind.Module -> AllIcons.Nodes.Module // Feature
            SymbolKind.Constructor -> AllIcons.Nodes.Method // Background
            SymbolKind.Namespace -> AllIcons.Nodes.Package // Rule
            SymbolKind.Method -> AllIcons.Nodes.Method // Scenario / Scenario Outline
            SymbolKind.Field -> AllIcons.Nodes.Field // Step
            SymbolKind.Array -> AllIcons.Nodes.Parameter // Examples
            else -> AllIcons.Nodes.Unknown
        }
    }
}
