package net.reqnroll.idesupport.rider.psi

import com.intellij.lexer.Lexer
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.tree.IElementType
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/**
 * Colours a feature file from its tokens as soon as it opens, before the language server has answered. The
 * colours are the ones the server's semantic tokens use ([ReqnrollFeatureTextAttributes]), so the file does not
 * change colour when those arrive. What only the server knows — step parameters, undefined and ambiguous steps —
 * is added on top of this by the semantic tokens.
 */
class ReqnrollFeatureSyntaxHighlighter(
    private val defaultLanguage: String = FeatureSyntax.FALLBACK_LANGUAGE,
) : SyntaxHighlighterBase() {
    override fun getHighlightingLexer(): Lexer = ReqnrollFeatureLexer(defaultLanguage)

    override fun getTokenHighlights(tokenType: IElementType?): Array<TextAttributesKey> =
        attributesByType[tokenType]?.let { arrayOf(it) } ?: emptyArray()

    internal companion object {
        /** The colour of each token kind. Kinds that are absent are drawn in the editor's plain text colour. */
        val attributesByKind: Map<GherkinTokenKind, TextAttributesKey> = mapOf(
            GherkinTokenKind.LANGUAGE_HEADER to ReqnrollFeatureTextAttributes.COMMENT,
            GherkinTokenKind.COMMENT to ReqnrollFeatureTextAttributes.COMMENT,
            GherkinTokenKind.TAG to ReqnrollFeatureTextAttributes.TAG,
            GherkinTokenKind.KEYWORD to ReqnrollFeatureTextAttributes.KEYWORD,
            GherkinTokenKind.STEP_KEYWORD to ReqnrollFeatureTextAttributes.KEYWORD,
            GherkinTokenKind.PLACEHOLDER to ReqnrollFeatureTextAttributes.SCENARIO_OUTLINE_PLACEHOLDER,
            GherkinTokenKind.HEADER_CELL to ReqnrollFeatureTextAttributes.DATA_TABLE_HEADER,
            GherkinTokenKind.CELL to ReqnrollFeatureTextAttributes.DATA_TABLE,
            GherkinTokenKind.DOC_STRING_DELIMITER to ReqnrollFeatureTextAttributes.DOC_STRING,
            GherkinTokenKind.DOC_STRING_TYPE to ReqnrollFeatureTextAttributes.DOC_STRING,
            GherkinTokenKind.DOC_STRING_CONTENT to ReqnrollFeatureTextAttributes.DOC_STRING,
            GherkinTokenKind.DESCRIPTION to ReqnrollFeatureTextAttributes.DESCRIPTION,
        )

        private val attributesByType: Map<IElementType, TextAttributesKey> =
            attributesByKind.mapKeys { ReqnrollFeatureTokenTypes.of(it.key) }
    }
}

/** Creates the highlighter for a file, using the default dialect of the file's project. */
class ReqnrollFeatureSyntaxHighlighterFactory : SyntaxHighlighterFactory() {
    override fun getSyntaxHighlighter(project: Project?, virtualFile: VirtualFile?): SyntaxHighlighter =
        ReqnrollFeatureSyntaxHighlighter(FeatureDefaultLanguages.languageFor(virtualFile))
}
