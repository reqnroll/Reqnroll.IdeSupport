package com.reqnroll.ide.rider.testrunner

import com.intellij.codeInsight.codeVision.CodeVisionAnchorKind
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.CodeVisionProvider
import com.intellij.codeInsight.codeVision.CodeVisionRelativeOrdering
import com.intellij.codeInsight.codeVision.CodeVisionState
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.reqnroll.ide.rider.codevision.EditorLensRefresh

/**
 * "▶ Run" CodeVision lens on each Scenario/Scenario Outline line in `.feature` files (design doc
 * §5/§6, issue #262) — the Rider counterpart to VS Code's Run CodeLens / VS's classic Run CodeLens.
 *
 * The design doc's original Rider recommendation was `RunLineMarkerContributor` (a true left-gutter
 * icon). That API is inherently PSI-based (`getInfo(PsiElement)`, invoked as the platform walks a
 * file's PSI tree) and this plugin registers `.feature` with no `ParserDefinition` at all — see
 * [com.reqnroll.ide.rider.ReqnrollFeatureLanguage]'s own doc comment — so it has no PSI tree to
 * walk. `CodeVisionProvider` is used instead: it's PSI-free (operates on `Editor`/`Document`
 * offsets, same as every other `.feature` editor feature in this plugin), and it's the exact
 * mechanism already proven out for the closely analogous hook-match-count lens
 * ([com.reqnroll.ide.rider.codevision.HookCodeVisionProvider]), which this class mirrors directly.
 *
 * Clicking the lens shells to `dotnet test --filter` itself (via [RunTestRunner]) rather than
 * integrating with Rider's native Test Runner (`SMTestProxy`) — this plugin has no
 * `com.intellij.execution`/`RunConfiguration` infrastructure at all yet, and native integration was
 * already flagged as needing a live devcontainer session to verify; own execution mirrors VS Code's
 * already-shipped "Option 2" for this same issue.
 */
class RunTestCodeVisionProvider : CodeVisionProvider<Unit> {
    companion object {
        const val ID = "Reqnroll.RunTestCodeVision"

        /** Forces a recompute of this lens for every currently open `.feature` editor in [project] — called after a run completes so the lens picks up the new pass/fail state. */
        fun refreshOpenFeatureEditors(project: Project) =
            EditorLensRefresh.invalidate(project, "feature", listOf(ID))
    }

    override val id: String = ID
    override val name: String = "Reqnroll run test"

    override val relativeOrderings: List<CodeVisionRelativeOrdering> = emptyList()
    override val defaultAnchor: CodeVisionAnchorKind = CodeVisionAnchorKind.Default

    override fun precomputeOnUiThread(editor: Editor) = Unit

    override fun computeCodeVision(editor: Editor, uiData: Unit): CodeVisionState =
        CodeVisionState.Ready(computeEntries(editor))

    private fun computeEntries(editor: Editor): List<Pair<TextRange, CodeVisionEntry>> {
        val project = editor.project ?: return emptyList()
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return emptyList()
        if (!file.extension.equals("feature", ignoreCase = true)) return emptyList()

        return RunLensSupport.computeEntries(project, editor.document, file.path, id)
    }
}
