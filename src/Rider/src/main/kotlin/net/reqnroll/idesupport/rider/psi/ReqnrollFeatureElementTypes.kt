package net.reqnroll.idesupport.rider.psi

import com.intellij.lang.ASTNode
import com.intellij.lang.PsiBuilderFactory
import com.intellij.psi.PsiElement
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType
import net.reqnroll.idesupport.rider.ReqnrollFeatureLanguage
import net.reqnroll.idesupport.rider.gherkin.GherkinTokenKind

/** Token types the feature lexer produces: one per [GherkinTokenKind], plus text the grammar could not place. */
object ReqnrollFeatureTokenTypes {
    /** Text the Gherkin parser skipped while recovering from a syntax error. */
    @JvmField
    val UNMATCHED = IElementType("REQNROLL_FEATURE_UNMATCHED", ReqnrollFeatureLanguage)

    private val byKind: Map<GherkinTokenKind, IElementType> = GherkinTokenKind.entries.associateWith {
        IElementType("REQNROLL_FEATURE_${it.name}", ReqnrollFeatureLanguage)
    }

    /** The token type for [kind]. */
    fun of(kind: GherkinTokenKind): IElementType = byKind.getValue(kind)
}

/**
 * The file element type. It parses the file's contents itself so the lexer and parser can be given the default
 * dialect of the file's project, which a [com.intellij.lang.ParserDefinition]'s project-wide lexer cannot know.
 */
class ReqnrollFeatureFileElementType : IFileElementType("REQNROLL_FEATURE_FILE", ReqnrollFeatureLanguage) {
    override fun doParseContents(chameleon: ASTNode, psi: PsiElement): ASTNode? {
        val language = FeatureDefaultLanguages.languageFor(psi.containingFile?.viewProvider?.virtualFile)
        val builder = PsiBuilderFactory.getInstance().createBuilder(
            psi.project, chameleon, ReqnrollFeatureLexer(language), ReqnrollFeatureLanguage, chameleon.chars,
        )
        return ReqnrollFeatureParser(language).parse(this, builder).firstChildNode
    }
}

/** Composite element types of a feature file's tree: the file itself and one per Gherkin grammar rule. */
object ReqnrollFeatureElementTypes {
    @JvmField
    val FILE: IFileElementType = ReqnrollFeatureFileElementType()

    /** The grammar rules the Gherkin parser reports, by the library's own rule names. */
    internal val knownRules: List<String> = listOf(
        "Feature", "FeatureHeader", "Rule", "RuleHeader", "Background",
        "ScenarioDefinition", "Scenario", "ExamplesDefinition", "Examples", "ExamplesTable",
        "Step", "StepArg", "DataTableAndMaybeDocString", "DocStringAndMaybeDataTable",
        "DataTable", "DocString", "Tags", "Description", "DescriptionHelper",
    )

    private val byRule: Map<String, IElementType> = knownRules.associateWith {
        IElementType("REQNROLL_FEATURE_RULE_${it.uppercase()}", ReqnrollFeatureLanguage)
    }

    /** Used for a rule name this plugin does not know, so a newer parser version cannot break tree building. */
    private val unknownRule = IElementType("REQNROLL_FEATURE_RULE_OTHER", ReqnrollFeatureLanguage)

    /** The element type for the grammar rule called [name]. */
    fun forRule(name: String): IElementType = byRule[name] ?: unknownRule
}
