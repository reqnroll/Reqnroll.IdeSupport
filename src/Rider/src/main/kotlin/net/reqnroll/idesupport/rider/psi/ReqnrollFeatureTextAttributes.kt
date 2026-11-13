package net.reqnroll.idesupport.rider.psi

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.CodeInsightColors
import com.intellij.openapi.editor.colors.TextAttributesKey

/**
 * The editor colours of a feature file, one per kind of span.
 *
 * They are shared by the lexer-based highlighter ([ReqnrollFeatureSyntaxHighlighter]), which colours a file
 * before the language server has answered, and by the server's semantic tokens
 * (`ReqnrollSemanticTokensSupport`), which refine it afterwards. Using the same keys is what keeps the file from
 * changing colour when the first tokens arrive. Defaults mirror the intent, not the pixels, of VS Code's
 * `semanticTokenScopes` mapping via the closest existing platform colours.
 */
object ReqnrollFeatureTextAttributes {
    @JvmField
    val KEYWORD: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_KEYWORD", DefaultLanguageHighlighterColors.KEYWORD)

    @JvmField
    val TAG: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_TAG", DefaultLanguageHighlighterColors.METADATA)

    @JvmField
    val DESCRIPTION: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_DESCRIPTION", DefaultLanguageHighlighterColors.DOC_COMMENT)

    @JvmField
    val COMMENT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_COMMENT", DefaultLanguageHighlighterColors.LINE_COMMENT)

    @JvmField
    val DOC_STRING: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_DOC_STRING", DefaultLanguageHighlighterColors.STRING)

    @JvmField
    val DATA_TABLE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_DATA_TABLE", DefaultLanguageHighlighterColors.STRING)

    @JvmField
    val DATA_TABLE_HEADER: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_DATA_TABLE_HEADER", DefaultLanguageHighlighterColors.CONSTANT)

    /**
     * NUMBER rather than PARAMETER: most default colour schemes leave "Parameter" with little or no distinct
     * styling, while NUMBER renders distinctly in every default IntelliJ and Rider scheme (confirmed in live
     * testing).
     */
    @JvmField
    val STEP_PARAMETER: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_STEP_PARAMETER", DefaultLanguageHighlighterColors.NUMBER)

    /**
     * Shares NUMBER with [STEP_PARAMETER] because the two never occur on the same step, and it matches the Visual
     * Studio extension's own colouring, which bases placeholders on "number" too.
     */
    @JvmField
    val SCENARIO_OUTLINE_PLACEHOLDER: TextAttributesKey = TextAttributesKey.createTextAttributesKey(
        "REQNROLL_SCENARIO_OUTLINE_PLACEHOLDER", DefaultLanguageHighlighterColors.NUMBER,
    )

    /**
     * ERRORS_ATTRIBUTES (a squiggly underline) rather than a background fill, which looks too much like the
     * caret-row highlight when the undefined step is also the active line. An undefined step will fail at run
     * time, so an error style fits. Only the server knows a step is undefined; the lexer never uses this.
     */
    @JvmField
    val UNDEFINED_STEP: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_UNDEFINED_STEP", CodeInsightColors.ERRORS_ATTRIBUTES)

    /** Only the server knows a step is ambiguous; the lexer never uses this. */
    @JvmField
    val AMBIGUOUS_STEP: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("REQNROLL_AMBIGUOUS_STEP", CodeInsightColors.WEAK_WARNING_ATTRIBUTES)
}
