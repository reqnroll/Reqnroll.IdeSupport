package com.reqnroll.ide.rider.codevision

import com.intellij.codeInsight.codeVision.CodeVisionAnchorKind
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.CodeVisionProvider
import com.intellij.codeInsight.codeVision.CodeVisionRelativeOrdering
import com.intellij.codeInsight.codeVision.CodeVisionState
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.util.TextRange

/**
 * "N step hooks" CodeVision lens — the consolidated per-scenario step-hooks count (issue #372)
 * shown alongside [HookCodeVisionProvider]'s Scenario-only "N hooks" lens on the same `Scenario:`
 * line. A *separate* registered provider rather than a second entry from `HookCodeVisionProvider`
 * itself: see [HookLensSupport]'s doc comment for why a single provider can't reliably show two
 * entries on one line, but two separately registered providers compose side by side automatically
 * (the same mechanism that shows the built-in "N usages" lens next to
 * [StepUsagesCodeVisionProvider]'s "N step usages" lens on a `.cs` method line).
 *
 * [refreshOpenFeatureEditors] lives on [HookCodeVisionProvider] and invalidates both providers'
 * ids together, since they always recompute from the same `workspace/codeLens/refresh` signal.
 */
class StepHooksCodeVisionProvider : CodeVisionProvider<Unit> {
    companion object {
        const val ID = "Reqnroll.StepHooksCodeVision"
    }

    override val id: String = ID
    override val name: String = "Reqnroll step hook matches"

    // Ordered after HookCodeVisionProvider so the Scenario-only count always appears first,
    // matching HookCodeLensHandler.cs's emission order (own-level lens, then step-hooks lens).
    override val relativeOrderings: List<CodeVisionRelativeOrdering> =
        listOf(CodeVisionRelativeOrdering.CodeVisionRelativeOrderingAfter(HookCodeVisionProvider.ID))
    override val defaultAnchor: CodeVisionAnchorKind = CodeVisionAnchorKind.Default

    override fun precomputeOnUiThread(editor: Editor) = Unit

    override fun computeCodeVision(editor: Editor, uiData: Unit): CodeVisionState =
        CodeVisionState.Ready(computeEntries(editor))

    private fun computeEntries(editor: Editor): List<Pair<TextRange, CodeVisionEntry>> {
        val project = editor.project ?: return emptyList()
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return emptyList()
        if (!file.extension.equals("feature", ignoreCase = true)) return emptyList()

        return HookLensSupport.computeEntries(project, editor.document, file.path, id, wantStepHooksLens = true)
    }
}
