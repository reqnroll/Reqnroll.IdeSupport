package net.reqnroll.idesupport.rider.psi

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

/** Composite element types of a feature file's tree: the file itself and one per Gherkin grammar rule. */
object ReqnrollFeatureElementTypes {
    @JvmField
    val FILE = IFileElementType("REQNROLL_FEATURE_FILE", ReqnrollFeatureLanguage)

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
