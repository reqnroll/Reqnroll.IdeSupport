package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
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
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender

/**
 * "N hooks" CodeVision lens above each `Feature:`/`Scenario:` line in `.feature` files
 * (issue #269) — the Rider equivalent of VS Code's hook-match CodeLens, and a sibling to
 * [StepUsagesCodeVisionProvider] (see that class's doc comment for why Rider needs a native
 * `CodeVisionProvider` at all rather than consuming generic `textDocument/codeLens`: Rider's LSP
 * client has no rendering-side consumer for it). Clicking a lens invokes the same
 * [GoToHooksRunner] "Go to Hooks" flow the dedicated action already uses.
 *
 * As of issue #372, the server no longer emits one lens per line with matching display/click
 * positions: `HookCodeLensHandler.cs` now counts only hooks native to each level (no cumulative
 * bleed) and consolidates every step's hooks into a single second lens shown on the `Scenario:`
 * line. Its click target — and an `ownLevelOnly` flag so "Go to Hooks" filters to match the
 * lens's own count — travel in `command.arguments`, not `lens.range`; see [computeEntries].
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
         * Computes the offset for a lens whose display line already has [priorEntriesOnLine]
         * entries rendered before it, so that entries sharing a display line never collide on
         * `TextRange.equals()` (see [computeEntries]'s doc comment for why that matters).
         * Clamped to [lineEndOffset] so the nudge never spills onto the next line.
         * `internal` (rather than private) purely so it's unit-testable without an Editor/Document fixture.
         */
        internal fun dedupedOffset(
            lineStartOffset: Int, displayCharacter: Int, priorEntriesOnLine: Int, lineEndOffset: Int,
        ): Int = (lineStartOffset + displayCharacter + priorEntriesOnLine).coerceAtMost(lineEndOffset)

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
        ReqnrollDebugLogger.info(
            "HookCodeVisionProvider: raw codeLens response for $uri: " +
                lenses.joinToString { l -> "[title=${l.command?.title}, range=${l.range}, args=${l.command?.arguments}]" },
        )

        val document = editor.document
        val renderable = lenses.filter { StepUsagesCodeVisionProvider.isRenderable(it, document.lineCount) }

        // Two lenses can share the exact same display position — e.g. the Scenario-only "N
        // hooks" lens and the consolidated step-hooks lens both anchor on the Scenario: line
        // (see HookCodeLensHandler.cs). VS Code's CodeLens renders multiple entries at an
        // identical range side by side, but IntelliJ's CodeVision keys entries by TextRange, so
        // two zero-length ranges at the exact same offset collide (only one survives to render,
        // and it isn't necessarily paired with its own click handler). dedupedOffset nudges every
        // entry after the first on a given line so their ranges stay distinct while still
        // resolving to the same visual line.
        val priorEntriesPerLine = HashMap<Int, Int>()

        return renderable.map { lens ->
            val command = lens.command!!

            // Display position: where the lens is anchored (Feature:/Scenario: line).
            val displayLine = lens.range.start.line
            val displayCharacter = lens.range.start.character
            val priorEntriesOnLine = priorEntriesPerLine.getOrDefault(displayLine, 0)
            priorEntriesPerLine[displayLine] = priorEntriesOnLine + 1
            val offset = dedupedOffset(
                document.getLineStartOffset(displayLine), displayCharacter,
                priorEntriesOnLine, document.getLineEndOffset(displayLine),
            )

            // Click target + filter: HookCodeLensHandler.cs encodes
            // (uri, line, character, ownLevelOnly) in command.arguments. The click
            // position can differ from the display position — e.g. the consolidated
            // step-hooks lens is *shown* on the Scenario: line but *navigates* to the
            // scenario's first step so "Go to Hooks" resolves Step context — so this must
            // read arguments rather than reuse lens.range. Falls back to the display
            // position/false if arguments are absent (defensive; the server always sends
            // all four).
            val arguments = command.arguments
            val clickLine = argAsInt(arguments, 1) ?: displayLine
            val clickCharacter = argAsInt(arguments, 2) ?: displayCharacter
            val ownLevelOnly = argAsBoolean(arguments, 3) ?: false
            ReqnrollDebugLogger.info(
                "HookCodeVisionProvider: arg runtime types = " +
                    (arguments?.map { it?.javaClass?.name } ?: listOf("<null arguments>")),
            )

            val entry = StepUsagesCodeVisionProvider.buildEntry(command, id) {
                GoToHooksRunner.runAndShow(project, uri, clickLine, clickCharacter, ownLevelOnly)
            }
            ReqnrollDebugLogger.info(
                "HookCodeVisionProvider: computed entry title='${entry.text}' offset=$offset " +
                    "(displayLine=$displayLine, priorEntriesOnLine=$priorEntriesOnLine) " +
                    "click=($clickLine,$clickCharacter) ownLevelOnly=$ownLevelOnly",
            )
            TextRange(offset, offset) to entry
        }
    }
}
