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
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.actions.GoToHooksRunner
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender

/**
 * "N hooks" CodeVision lens above each `Feature:`/`Scenario:`/step line in `.feature` files
 * (issue #269) — the Rider equivalent of VS Code's hook-match CodeLens, and a sibling to
 * [StepUsagesCodeVisionProvider] (see that class's doc comment for why Rider needs a native
 * `CodeVisionProvider` at all rather than consuming generic `textDocument/codeLens`: Rider's LSP
 * client has no rendering-side consumer for it). Clicking a lens invokes the same
 * [GoToHooksRunner] "Go to Hooks" flow the dedicated action already uses, at that lens's line.
 */
class HookCodeVisionProvider : CodeVisionProvider<Unit> {
    companion object {
        private const val ID = "Reqnroll.HookCodeVision"

        /**
         * Forces a recompute of this lens for every currently open `.feature` editor in
         * [project] — called from [com.reqnroll.ide.rider.lsp.ReqnrollCodeLensRefreshInterceptor]
         * when the server sends `workspace/codeLens/refresh`, the same way
         * [StepUsagesCodeVisionProvider.refreshOpenCsEditors] does for `.cs` files.
         */
        fun refreshOpenFeatureEditors(project: Project) {
            val codeVisionHost = project.service<CodeVisionHost>()
            for (editor in EditorFactory.getInstance().allEditors) {
                if (editor.project != project) continue
                val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: continue
                if (!virtualFile.extension.equals("feature", ignoreCase = true)) continue
                codeVisionHost.invalidateProvider(CodeVisionHost.LensInvalidateSignal(editor, listOf(ID)))
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

        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(file.path))
        val lenses = ReqnrollRequestSender.codeLens(project, uri) ?: return emptyList()

        val document = editor.document
        return lenses
            .filter { StepUsagesCodeVisionProvider.isRenderable(it, document.lineCount) }
            .map { lens ->
                val command = lens.command!!
                val line = lens.range.start.line
                val character = lens.range.start.character
                val offset = document.getLineStartOffset(line) + character
                val entry = StepUsagesCodeVisionProvider.buildEntry(command, id) {
                    GoToHooksRunner.runAndShow(project, uri, line, character)
                }
                TextRange(offset, offset) to entry
            }
    }
}
