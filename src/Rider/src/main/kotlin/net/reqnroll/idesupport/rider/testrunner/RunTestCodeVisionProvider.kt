package net.reqnroll.idesupport.rider.testrunner

import com.intellij.codeInsight.codeVision.CodeVisionAnchorKind
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.CodeVisionProvider
import com.intellij.codeInsight.codeVision.CodeVisionRelativeOrdering
import com.intellij.codeInsight.codeVision.CodeVisionState
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import net.reqnroll.idesupport.rider.codevision.EditorLensRefresh

/**
 * "▶ Run" CodeVision lens on each Scenario/Scenario Outline line in `.feature` files (design doc
 * §5/§6) — the Rider counterpart to VS Code's Run CodeLens / VS's classic Run CodeLens.
 *
 * It is a PSI-free `CodeVisionProvider` (operating on `Editor`/`Document` offsets), the same
 * mechanism as the hook-match-count lens
 * ([net.reqnroll.idesupport.rider.codevision.HookCodeVisionProvider]), which this class mirrors.
 *
 * Clicking the lens shells to `dotnet test --filter` itself (via [RunTestRunner]) rather than
 * going through Rider's Unit Tests window. Where the bundled backend module is installed, Rider's own
 * test gutter marks run and debug the Feature and its scenarios through that window instead, so this
 * lens keeps only Rule lines there ([BackendTestGutterMarks]).
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

        return RunLensSupport.computeEntries(
            project, editor.document, file.path, id, rulesOnly = BackendTestGutterMarks.hideScenarioRunLens,
        )
    }
}
