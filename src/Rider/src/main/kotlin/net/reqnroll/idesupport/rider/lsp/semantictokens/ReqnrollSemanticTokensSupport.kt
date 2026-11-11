package net.reqnroll.idesupport.rider.lsp.semantictokens

import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import net.reqnroll.idesupport.rider.psi.ReqnrollFeatureTextAttributes

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
        // The keys, and the reasons behind each default colour, live in ReqnrollFeatureTextAttributes so the
        // lexer-based highlighter and these server tokens share them.
        private val KEYS: Map<String, TextAttributesKey> = mapOf(
            ReqnrollSemanticTokenTypes.KEYWORD to ReqnrollFeatureTextAttributes.KEYWORD,
            ReqnrollSemanticTokenTypes.TAG to ReqnrollFeatureTextAttributes.TAG,
            ReqnrollSemanticTokenTypes.DESCRIPTION to ReqnrollFeatureTextAttributes.DESCRIPTION,
            ReqnrollSemanticTokenTypes.COMMENT to ReqnrollFeatureTextAttributes.COMMENT,
            ReqnrollSemanticTokenTypes.DOC_STRING to ReqnrollFeatureTextAttributes.DOC_STRING,
            ReqnrollSemanticTokenTypes.DATA_TABLE to ReqnrollFeatureTextAttributes.DATA_TABLE,
            ReqnrollSemanticTokenTypes.DATA_TABLE_HEADER to ReqnrollFeatureTextAttributes.DATA_TABLE_HEADER,
            ReqnrollSemanticTokenTypes.STEP_PARAMETER to ReqnrollFeatureTextAttributes.STEP_PARAMETER,
            ReqnrollSemanticTokenTypes.SCENARIO_OUTLINE_PLACEHOLDER to
                ReqnrollFeatureTextAttributes.SCENARIO_OUTLINE_PLACEHOLDER,
            ReqnrollSemanticTokenTypes.UNDEFINED_STEP to ReqnrollFeatureTextAttributes.UNDEFINED_STEP,
            ReqnrollSemanticTokenTypes.AMBIGUOUS_STEP to ReqnrollFeatureTextAttributes.AMBIGUOUS_STEP,
        )
    }
}
