package net.reqnroll.idesupport.rider.structureview

import com.intellij.ide.structureView.FileEditorPositionListener
import com.intellij.ide.structureView.ModelListener
import com.intellij.ide.structureView.StructureViewModel
import com.intellij.ide.structureView.StructureViewTreeElement
import com.intellij.ide.structureView.TreeBasedStructureViewBuilder
import com.intellij.ide.util.treeView.smartTree.Filter
import com.intellij.ide.util.treeView.smartTree.Grouper
import com.intellij.ide.util.treeView.smartTree.Sorter
import com.intellij.ide.util.treeView.smartTree.TreeElement
import com.intellij.navigation.ItemPresentation
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.PsiManager

/**
 * Builds the Structure View model for one `.feature` [virtualFile]. Instantiated directly by
 * [ReqnrollStructurePanel] rather than resolved through the declarative
 * `com.intellij.structureViewBuilder` EP — see [ReqnrollStructureToolWindowFactory]'s doc comment
 * for why that EP doesn't work here.
 *
 * `isRootNodeShown() = false`: the model's root wraps the whole file (needed to satisfy
 * `StructureViewModelBase`'s constructor), but the file itself isn't a useful outline entry —
 * hiding it shows Feature/Rule/Scenario/Step directly as top-level tree nodes, matching every
 * other language's Structure View convention.
 */
class ReqnrollFeatureStructureViewBuilder(
    private val project: Project,
    private val virtualFile: VirtualFile,
) : TreeBasedStructureViewBuilder() {
    override fun createStructureViewModel(editor: Editor?): StructureViewModel {
        // Every VirtualFile with a registered FileType resolves to a real (if structurally
        // trivial, parser-less) PsiFile via PsiManager — confirmed elsewhere in this plugin (see
        // ReqnrollFeatureInlayHintsController's doc comment) — so this should not actually be null
        // for a `.feature` file in practice. StructureViewModelBase's constructor requires a
        // non-null PsiFile regardless, so we fall back to an empty model if it somehow is null
        // (e.g. during project disposal).
        val psiFile = PsiManager.getInstance(project).findFile(virtualFile) ?: return emptyViewModel()
        return ReqnrollFeatureStructureViewModel(project, virtualFile, psiFile, editor)
    }

    private fun emptyViewModel() = object : StructureViewModel {
        override fun getRoot(): StructureViewTreeElement = object : StructureViewTreeElement {
            override fun getChildren(): Array<TreeElement> = emptyArray()
            override fun getValue(): Any = this
            override fun getPresentation(): ItemPresentation = object : ItemPresentation {
                override fun getPresentableText(): String = virtualFile.name
                override fun getIcon(unused: Boolean) = null
            }
            override fun navigate(requestFocus: Boolean) {}
            override fun canNavigate(): Boolean = false
            override fun canNavigateToSource(): Boolean = false
        }
        override fun getGroupers(): Array<Grouper> = emptyArray()
        override fun getSorters(): Array<Sorter> = emptyArray()
        override fun getFilters(): Array<Filter> = emptyArray()
        override fun getCurrentEditorElement(): Any? = null
        override fun addEditorPositionListener(listener: FileEditorPositionListener) {}
        override fun removeEditorPositionListener(listener: FileEditorPositionListener) {}
        override fun addModelListener(listener: ModelListener) {}
        override fun removeModelListener(listener: ModelListener) {}
        override fun dispose() {}
        override fun shouldEnterElement(element: Any?): Boolean = false
    }

    override fun isRootNodeShown(): Boolean = false
}
