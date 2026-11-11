package net.reqnroll.idesupport.rider.psi

import com.intellij.extapi.psi.ASTWrapperPsiElement
import com.intellij.extapi.psi.PsiFileBase
import com.intellij.lang.ASTNode
import com.intellij.lang.ParserDefinition
import com.intellij.lang.PsiParser
import com.intellij.lexer.Lexer
import com.intellij.openapi.fileTypes.FileType
import com.intellij.openapi.project.Project
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IFileElementType
import com.intellij.psi.tree.TokenSet
import net.reqnroll.idesupport.rider.ReqnrollFeatureFileType
import net.reqnroll.idesupport.rider.ReqnrollFeatureLanguage
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/** A parsed `.feature` file. */
class ReqnrollFeaturePsiFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, ReqnrollFeatureLanguage) {
    override fun getFileType(): FileType = ReqnrollFeatureFileType

    override fun toString(): String = "Reqnroll feature file"
}

/** Gives `.feature` files a lexer, a tree parser and a file element, built on the Cucumber Gherkin parser. */
class ReqnrollFeatureParserDefinition : ParserDefinition {
    override fun createLexer(project: Project?): Lexer = ReqnrollFeatureLexer()

    override fun createParser(project: Project?): PsiParser = ReqnrollFeatureParser()

    override fun getFileNodeType(): IFileElementType = ReqnrollFeatureElementTypes.FILE

    override fun getWhitespaceTokens(): TokenSet = TokenSet.create(TokenType.WHITE_SPACE)

    override fun getCommentTokens(): TokenSet = TokenSet.create(
        ReqnrollFeatureTokenTypes.of(GherkinTokenKind.COMMENT),
        ReqnrollFeatureTokenTypes.of(GherkinTokenKind.LANGUAGE_HEADER),
    )

    override fun getStringLiteralElements(): TokenSet = TokenSet.EMPTY

    override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)

    override fun createFile(viewProvider: FileViewProvider): PsiFile = ReqnrollFeaturePsiFile(viewProvider)
}
