package com.reqnroll.ide.rider.codevision

import com.intellij.codeInsight.codeVision.CodeVisionAnchorKind
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.CodeVisionHost
import com.intellij.codeInsight.codeVision.CodeVisionProvider
import com.intellij.codeInsight.codeVision.CodeVisionRelativeOrdering
import com.intellij.codeInsight.codeVision.CodeVisionState
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange

/**
 * "N hooks" CodeVision lens above each `Feature:`/`Scenario:` line in `.feature` files (issue
 * #269) — the Rider equivalent of VS Code's hook-match CodeLens, and a sibling to
 * [StepUsagesCodeVisionProvider] (see that class's doc comment for why Rider needs a native
 * `CodeVisionProvider` at all rather than consuming generic `textDocument/codeLens`: Rider's LSP
 * client has no rendering-side consumer for it). Clicking a lens invokes the same
 * `GoToHooksRunner` "Go to Hooks" flow the dedicated action already uses.
 *
 * Shows only the Feature-only/Scenario-only hook count — the consolidated step-hooks count (issue
 * #372) is [StepHooksCodeVisionProvider], a *separate* registered provider. See
 * [HookLensSupport]'s doc comment for why: a single provider can't reliably show two entries on
 * the same `Scenario:` line, but two separately registered providers compose side by side
 * automatically, same as the built-in "N usages" lens next to [StepUsagesCodeVisionProvider]'s
 * on a `.cs` method line.
 */
class HookCodeVisionProvider : CodeVisionProvider<Unit> {
    companion object {
        const val ID = "Reqnroll.HookCodeVision"

        /**
         * Forces a recompute of this lens (and [StepHooksCodeVisionProvider]'s) for every
         * currently open `.feature` editor in [project] — called from
         * [com.reqnroll.ide.rider.lsp.ReqnrollCodeLensRefreshInterceptor] when the server sends
         * `workspace/codeLens/refresh`, the same way
         * [StepUsagesCodeVisionProvider.refreshOpenCsEditors] does for `.cs` files.
         */
        fun refreshOpenFeatureEditors(project: Project) {
            val codeVisionHost = project.service<CodeVisionHost>()
            for (editor in EditorFactory.getInstance().allEditors) {
                if (editor.project != project) continue
                val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: continue
                if (!virtualFile.extension.equals("feature", ignoreCase = true)) continue
                codeVisionHost.invalidateProvider(
                    CodeVisionHost.LensInvalidateSignal(editor, listOf(ID, StepHooksCodeVisionProvider.ID)),
                )
            }
        }
    }

    override val id: String = ID
    override val name: String = "Reqnroll hook matches"

    override val relativeOrderings: List<CodeVisionRelativeOrdering> = emptyList()
    override val defaultAnchor: CodeVisionAnchorKind = CodeVisionAnchorKind.Default

    override fun precomputeOnUiThread(editor: Editor) = Unit

    override fun computeCodeVision(editor: Editor, uiData: Unit): CodeVisionState =
        CodeVisionState.Ready(computeEntries(editor))

    private fun computeEntries(editor: Editor): List<Pair<TextRange, CodeVisionEntry>> {
        val project = editor.project ?: return emptyList()
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return emptyList()
        if (!file.extension.equals("feature", ignoreCase = true)) return emptyList()

        return HookLensSupport.computeEntries(project, editor.document, file.path, id, wantStepHooksLens = false)
    }
}
