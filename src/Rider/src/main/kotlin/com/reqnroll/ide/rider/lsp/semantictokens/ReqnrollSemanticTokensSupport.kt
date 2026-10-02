package com.reqnroll.ide.rider.lsp.semantictokens

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.CodeInsightColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport

/**
 * The custom `reqnroll.*` semantic-token type names, matching
 * `Reqnroll.IdeSupport.Common.Classification.ReqnrollClassificationTypeNames` (server-side single
 * source of truth) field-for-field, in the same append-only legend order.
 */
object ReqnrollSemanticTokenTypes {
    const val KEYWORD = "reqnroll.keyword"
    const val TAG = "reqnroll.tag"
    const val DESCRIPTION = "reqnroll.description"
    const val COMMENT = "reqnroll.comment"
    const val DOC_STRING = "reqnroll.doc_string"
    const val DATA_TABLE = "reqnroll.data_table"
    const val DATA_TABLE_HEADER = "reqnroll.data_table_header"
    const val STEP_PARAMETER = "reqnroll.step_parameter"
    const val SCENARIO_OUTLINE_PLACEHOLDER = "reqnroll.scenario_outline_placeholder"
    const val UNDEFINED_STEP = "reqnroll.undefined_step"
    const val AMBIGUOUS_STEP = "reqnroll.ambiguous_step"

    val ORDERED: List<String> = listOf(
        KEYWORD, TAG, DESCRIPTION, COMMENT, DOC_STRING, DATA_TABLE, DATA_TABLE_HEADER,
        STEP_PARAMETER, SCENARIO_OUTLINE_PLACEHOLDER, UNDEFINED_STEP, AMBIGUOUS_STEP,
    )
}

/**
 * Maps the server's custom `reqnroll.*` semantic-token types to editor colors, since Rider's
 * platform default (`LspSemanticTokensSupport`) only recognizes the LSP *standard* token type
 * vocabulary (`keyword`, `string`, `parameter`, …, confirmed by decompiling Rider 2024.3.5's
 * actual `getTextAttributesKey` — a fixed `switch` over ~23 hardcoded standard names) and falls
 * through to no color for anything else. This is the same class of problem VS's built-in
 * semantic-token colorizer has (see `SemanticTokensPushHandler` on the server) — Rider's own
 * server-side doc comment on `ReqnrollSemanticTokens` already anticipated the fix: "Rider
 * registers a TextAttributesKey per name and maps via the LSP descriptor."
 *
 * Wired in via `ReqnrollLspServerDescriptor.lspSemanticTokensSupport`. Default colors below
 * mirror the intent (not pixel-parity) of VS Code's `semanticTokenScopes` mapping
 * (`src/VSCode/package.json`) via the closest existing `DefaultLanguageHighlighterColors`/
 * `CodeInsightColors` — refinable later via a dedicated color settings page /
 * `AdditionalTextAttributesProvider` if exact parity with VS's `DeveroomClassifications` matters.
 */
class ReqnrollSemanticTokensSupport : LspSemanticTokensSupport() {
    // Kotlin `open val` in the superclass (same lesson as ReqnrollLspServerDescriptor's
    // lsp4jServerClass) — must override as `val`, not `fun getXxx()`.
    override val tokenTypes: List<String> = ReqnrollSemanticTokenTypes.ORDERED

    override val tokenModifiers: List<String> = emptyList()

    override fun getTextAttributesKey(tokenType: String, modifiers: List<String>): TextAttributesKey? =
        KEYS[tokenType]

    companion object {
        private val KEYS: Map<String, TextAttributesKey> = mapOf(
            ReqnrollSemanticTokenTypes.KEYWORD to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD),
            ReqnrollSemanticTokenTypes.TAG to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_TAG", DefaultLanguageHighlighterColors.METADATA),
            ReqnrollSemanticTokenTypes.DESCRIPTION to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_DESCRIPTION", DefaultLanguageHighlighterColors.DOC_COMMENT),
            ReqnrollSemanticTokenTypes.COMMENT to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_COMMENT", DefaultLanguageHighlighterColors.LINE_COMMENT),
            ReqnrollSemanticTokenTypes.DOC_STRING to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_DOC_STRING", DefaultLanguageHighlighterColors.STRING),
            ReqnrollSemanticTokenTypes.DATA_TABLE to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_DATA_TABLE", DefaultLanguageHighlighterColors.STRING),
            ReqnrollSemanticTokenTypes.DATA_TABLE_HEADER to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_DATA_TABLE_HEADER", DefaultLanguageHighlighterColors.CONSTANT),
            // DefaultLanguageHighlighterColors.PARAMETER was the original choice, but most default
            // color schemes leave "Parameter" with little or no distinct styling (unlike the other
            // keys below, which are all strongly colored everywhere) — confirmed via live testing
            // (2026-07-13): tokens were arriving and mapped correctly, just not visually distinct
            // until the color scheme's Parameter style was customized. NUMBER renders distinctly in
            // every default IntelliJ/Rider scheme.
            ReqnrollSemanticTokenTypes.STEP_PARAMETER to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_STEP_PARAMETER", DefaultLanguageHighlighterColors.NUMBER),
            // INSTANCE_FIELD was the original choice, but it's the same class of "soft" fallback
            // that bit STEP_PARAMETER's original PARAMETER mapping above — no distinct default
            // styling in most schemes. NUMBER renders distinctly everywhere; reusing it here
            // (rather than picking a third arbitrary key) also matches the VS extension's own
            // DeveroomClassifications, which bases ScenarioOutlinePlaceholder on "number" too
            // (plus italic). Safe to share with STEP_PARAMETER's color since the two never
            // co-occur on the same step (DeveroomTagParser skips StepParameter tags entirely for
            // scenario outline steps whose text contains a placeholder).
            ReqnrollSemanticTokenTypes.SCENARIO_OUTLINE_PLACEHOLDER to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_SCENARIO_OUTLINE_PLACEHOLDER", DefaultLanguageHighlighterColors.NUMBER),
            // WARNINGS_ATTRIBUTES (a background-fill style) was the original choice, but it
            // renders close enough to the IDE's own caret-row highlight to be confusing when the
            // undefined step also happens to be the active line — confirmed via live testing
            // (2026-07-13). ERRORS_ATTRIBUTES (squiggly-underline style) is visually distinct from
            // any row-background highlight, and a better semantic fit anyway: an undefined step
            // genuinely will fail at runtime, not just a soft warning.
            ReqnrollSemanticTokenTypes.UNDEFINED_STEP to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_UNDEFINED_STEP", CodeInsightColors.ERRORS_ATTRIBUTES),
            ReqnrollSemanticTokenTypes.AMBIGUOUS_STEP to TextAttributesKey.createTextAttributesKey(
                "REQNROLL_AMBIGUOUS_STEP", CodeInsightColors.WEAK_WARNING_ATTRIBUTES),
        )
    }
}
