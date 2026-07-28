package com.reqnroll.ide.rider.codevision

import com.google.gson.JsonPrimitive
import com.intellij.codeInsight.codeVision.CodeVisionEntry
import com.intellij.openapi.editor.Document
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.util.io.URLUtil
import com.reqnroll.ide.rider.actions.GoToHooksRunner
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import org.eclipse.lsp4j.CodeLens

/**
 * Shared lens-computation logic for [HookCodeVisionProvider] and [StepHooksCodeVisionProvider] —
 * split into two separate `CodeVisionProvider` registrations (issue #372 follow-up) because a
 * *single* provider can't reliably show two entries on the same `Scenario:` line (empirically,
 * even distinct `TextRange`s on the same line only rendered the last one), whereas two separate
 * registered providers compose side by side automatically — the same platform mechanism that
 * already shows the built-in "N usages" lens next to [StepUsagesCodeVisionProvider]'s "N step
 * usages" lens on a `.cs` method line.
 *
 * `HookCodeLensHandler.cs` marks the consolidated step-hooks lens with a 5th `true` argument
 * (absent on the Feature-only/Scenario-only lens) so [isStepHooksLens] can split the flat
 * `textDocument/codeLens` response between the two providers reliably, rather than parsing title
 * text.
 */
internal object HookLensSupport {
    /** True for the consolidated step-hooks lens (see `HookCodeLensHandler.AddStepHooksLens`). */
    fun isStepHooksLens(lens: CodeLens): Boolean = argAsBoolean(lens.command?.arguments, 4) == true

    /**
     * Extracts `arguments[index]` as an [Int]. LSP4J's generic `Command.arguments`
     * (`List<Any>`) is a known source of surprises: elements aren't guaranteed to already be
     * plain boxed `java.lang.Number`/`Boolean` — the client's Gson deserialization can leave
     * them as raw [JsonPrimitive] instead, so a direct `as? Number` cast silently fails and
     * falls through to a default every time (confirmed via a diagnostic build: every click
     * fell back to the *display* position instead of the server-sent click target, which is
     * why every hook lens navigated identically regardless of which one was clicked).
     */
    fun argAsInt(arguments: List<Any>?, index: Int): Int? {
        val raw = arguments?.getOrNull(index) ?: return null
        return when {
            raw is Number -> raw.toInt()
            raw is JsonPrimitive && raw.isNumber -> raw.asInt
            else -> raw.toString().toIntOrNull()
        }
    }

    /** [Boolean] counterpart to [argAsInt] — see its doc comment for why this can't be a plain `as? Boolean` cast. */
    fun argAsBoolean(arguments: List<Any>?, index: Int): Boolean? {
        val raw = arguments?.getOrNull(index) ?: return null
        return when {
            raw is Boolean -> raw
            raw is JsonPrimitive && raw.isBoolean -> raw.asBoolean
            else -> raw.toString().toBooleanStrictOrNull()
        }
    }

    /**
     * Fetches the shared `textDocument/codeLens` response for [file]'s `.feature` document,
     * keeps only the lenses [wantStepHooksLens] selects (via [isStepHooksLens]), and maps each to
     * a `(TextRange, CodeVisionEntry)` pair anchored at its own display line — one entry per
     * matching lens, since (unlike the pre-split design) each provider now only ever contributes
     * at most one lens per line.
     */
    fun computeEntries(
        project: Project,
        document: Document,
        filePath: String,
        providerId: String,
        wantStepHooksLens: Boolean,
    ): List<Pair<TextRange, CodeVisionEntry>> {
        val uri = VirtualFileManager.constructUrl("file", URLUtil.encodePath(filePath))
        val lenses = ReqnrollRequestSender.codeLens(project, uri) ?: return emptyList()

        return lenses
            .filter { StepUsagesCodeVisionProvider.isRenderable(it, document.lineCount) }
            .filter { isStepHooksLens(it) == wantStepHooksLens }
            .map { lens ->
                val command = lens.command!!

                val displayLine = lens.range.start.line
                val displayCharacter = lens.range.start.character
                val offset = document.getLineStartOffset(displayLine) + displayCharacter

                // Click target + filter: HookCodeLensHandler.cs encodes
                // (uri, line, character, ownLevelOnly, ...) in command.arguments. The click
                // position can differ from the display position — the step-hooks lens is *shown*
                // on the Scenario: line but *navigates* to the scenario's first step so
                // "Go to Hooks" resolves Step context — so this must read arguments rather than
                // reuse lens.range. Falls back to the display position/false if arguments are
                // absent (defensive; the server always sends at least four).
                val arguments = command.arguments
                val clickLine = argAsInt(arguments, 1) ?: displayLine
                val clickCharacter = argAsInt(arguments, 2) ?: displayCharacter
                val ownLevelOnly = argAsBoolean(arguments, 3) ?: false

                val entry = StepUsagesCodeVisionProvider.buildEntry(command, providerId) {
                    GoToHooksRunner.runAndShow(project, uri, clickLine, clickCharacter, ownLevelOnly)
                }
                TextRange(offset, offset) to entry
            }
    }
}
