package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
import com.intellij.codeInsight.codeVision.CodeVisionAnchorKind
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.codeInsight.codeVision.CodeVisionHost
import com.intellij.codeInsight.codeVision.CodeVisionProvider
import com.intellij.codeInsight.codeVision.CodeVisionRelativeOrdering
import com.intellij.codeInsight.codeVision.CodeVisionState
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.actions.GoToHooksRunner
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.Command

/**
 * "N hooks" CodeVision lens above each `Feature:`/`Scenario:` line in `.feature` files
 * (issue #269) — the Rider equivalent of VS Code's hook-match CodeLens, and a sibling to
 * [StepUsagesCodeVisionProvider] (see that class's doc comment for why Rider needs a native
 * `CodeVisionProvider` at all rather than consuming generic `textDocument/codeLens`: Rider's LSP
 * client has no rendering-side consumer for it). Clicking a lens invokes the same
 * [GoToHooksRunner] "Go to Hooks" flow the dedicated action already uses.
 *
 * As of issue #372, `HookCodeLensHandler.cs` can return *two* lenses anchored on the same
 * `Scenario:` line: a scenario-only "N hooks" count and a second, consolidated "N step hooks"
 * count for every step in that scenario. VS Code's CodeLens renders multiple entries at an
 * identical range side by side, but IntelliJ's CodeVision does not support two entries from one
 * provider on the same line (confirmed by decompiling `RangeCodeVisionModel`/
 * `CodeVisionProvider` from the platform jar — and empirically: giving the two entries distinct
 * `TextRange`s on the same line still only rendered the last one). So instead of trying to show
 * two chips, [computeEntries] merges every lens on a given line into one combined entry — see its
 * doc comment for how the combined title and click target are chosen.
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

        /**
         * Extracts `arguments[index]` as an [Int]. LSP4J's generic `Command.arguments`
         * (`List<Any>`) is a known source of surprises: elements aren't guaranteed to already be
         * plain boxed `java.lang.Number`/`Boolean` — the client's Gson deserialization can leave
         * them as raw [JsonPrimitive] instead, so a direct `as? Number` cast silently fails and
         * falls through to a default every time (confirmed via a diagnostic build: every click
         * fell back to the *display* position instead of the server-sent click target, which is
         * why every hook lens navigated identically regardless of which one was clicked).
         * `internal` purely so it's unit-testable without a real LSP4J deserialization round-trip.
         */
        internal fun argAsInt(arguments: List<Any>?, index: Int): Int? {
            val raw = arguments?.getOrNull(index) ?: return null
            return when {
                raw is Number -> raw.toInt()
                raw is JsonPrimitive && raw.isNumber -> raw.asInt
                else -> raw.toString().toIntOrNull()
            }
        }

        /** [Boolean] counterpart to [argAsInt] — see its doc comment for why this can't be a plain `as? Boolean` cast. */
        internal fun argAsBoolean(arguments: List<Any>?, index: Int): Boolean? {
            val raw = arguments?.getOrNull(index) ?: return null
            return when {
                raw is Boolean -> raw
                raw is JsonPrimitive && raw.isBoolean -> raw.asBoolean
                else -> raw.toString().toBooleanStrictOrNull()
            }
        }

        /**
         * Joins every lens's title on one display line into a single combined label, e.g.
         * `"2 hooks · 1 step hook"`. `internal` purely so it's unit-testable.
         */
        internal fun combinedTitle(titles: List<String>): String = titles.joinToString(" · ")

        /**
         * Picks the click target to use for a merged entry: the candidate with the highest line
         * number. `GoToHooksHandler`'s cumulative hook sets are hierarchical (Step ⊇ Scenario ⊇
         * Feature — see `HookMatching.GetApplicableHookTypes`), so querying at the *deepest*
         * available position (e.g. the scenario's first step rather than the `Scenario:` line
         * itself) with `ownLevelOnly=false` returns every hook implied by the combined title,
         * not just whichever single lens happened to be picked. `internal` purely so it's
         * unit-testable. [candidates] must be non-empty.
         */
        internal fun richestClick(candidates: List<Pair<Int, Int>>): Pair<Int, Int> =
            candidates.maxBy { (line, _) -> line }
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
        val renderable = lenses.filter { StepUsagesCodeVisionProvider.isRenderable(it, document.lineCount) }

        return renderable
            .groupBy { it.range.start.line }
            .map { (displayLine, lensesOnLine) -> buildCombinedEntry(project, uri, document, displayLine, lensesOnLine) }
    }

    private fun buildCombinedEntry(
        project: Project, uri: String, document: Document, displayLine: Int, lensesOnLine: List<CodeLens>,
    ): Pair<TextRange, CodeVisionEntry> {
        val displayCharacter = lensesOnLine.first().range.start.character
        val offset = document.getLineStartOffset(displayLine) + displayCharacter

        val title = combinedTitle(lensesOnLine.map { it.command?.title ?: "" })
        val (clickLine, clickCharacter) = richestClick(
            lensesOnLine.map { lens ->
                val arguments = lens.command?.arguments
                (argAsInt(arguments, 1) ?: displayLine) to (argAsInt(arguments, 2) ?: displayCharacter)
            },
        )

        val command = Command(title, "reqnroll.goToHooks", listOf(uri, clickLine, clickCharacter, false))
        val entry = StepUsagesCodeVisionProvider.buildEntry(command, id) {
            GoToHooksRunner.runAndShow(project, uri, clickLine, clickCharacter, ownLevelOnly = false)
        }
        return TextRange(offset, offset) to entry
    }
}
