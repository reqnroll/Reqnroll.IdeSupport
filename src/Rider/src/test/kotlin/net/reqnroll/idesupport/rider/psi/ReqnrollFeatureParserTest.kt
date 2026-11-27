package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.PsiElement
import com.intellij.testFramework.ParsingTestCase

/**
 * Parses real text through a [ReqnrollFeatureParserDefinition] and checks the resulting tree. Uses
 * [ParsingTestCase], which runs on a lightweight mock project, because opening a real fixture project under
 * Rider's test target fails (see ReqnrollFeatureFileTypeRegistrationTest).
 */
class ReqnrollFeatureParserTest : ParsingTestCase("", "feature", ReqnrollFeatureParserDefinition()) {
    override fun getTestDataPath(): String = "."

    override fun skipSpaces(): Boolean = true

    override fun includeRanges(): Boolean = false

    private fun parse(text: String): PsiElement = parseFile("test.feature", text)

    private fun rulesOf(root: PsiElement): List<String> {
        val names = ArrayList<String>()
        fun visit(element: PsiElement) {
            val type = element.node.elementType
            if (type.toString().startsWith("REQNROLL_FEATURE_RULE_")) names.add(type.toString().removePrefix("REQNROLL_FEATURE_RULE_"))
            element.children.forEach(::visit)
        }
        visit(root)
        return names
    }

    fun `test tree covers the whole text and nests rules`() {
        val text = "@t\nFeature: f\n  Scenario: s\n    Given x\n      | a |\n"

        val file = parse(text)

        assertEquals(text, file.text)
        val rules = rulesOf(file)
        assertTrue(rules.toString(), "FEATURE" in rules && "SCENARIO" in rules && "STEP" in rules && "DATATABLE" in rules)
    }

    fun `test a step sits inside its scenario inside the feature`() {
        val file = parse("Feature: f\n  Scenario: s\n    Given x\n")

        val step = findRule(file, "STEP")

        assertNotNull(step)
        assertEquals("SCENARIO", generateSequence(step!!.parent) { it.parent }
            .map { it.node.elementType.toString().removePrefix("REQNROLL_FEATURE_RULE_") }
            .first { it == "SCENARIO" || it == "FEATURE" })
    }

    fun `test an unterminated doc string still gives a tree covering the text`() {
        val text = "Feature: f\n  Scenario: s\n    Given x\n      \"\"\"\n      open\n"

        val file = parse(text)

        assertEquals(text, file.text)
        assertNotNull(findRule(file, "DOCSTRING"))
    }

    fun `test lines the grammar skips are kept in the tree`() {
        val text = "Feature: f\n  Scenario: s\n    Given x\n  Bogus line here\n    Given y\n"

        val file = parse(text)

        assertEquals(text, file.text)
    }

    fun `test empty text parses to an empty file`() {
        val file = parse("")

        assertEquals("", file.text)
    }

    fun `test every corpus file round-trips its text`() {
        val dirs = listOf("good", "bad")
        for (dir in dirs) {
            val folder = java.io.File(javaClass.getResource("/gherkin-corpus/$dir")!!.toURI())
            for (file in folder.listFiles { f -> f.extension == "feature" }!!.sortedBy { it.name }) {
                val text = file.readText().replace("\r\n", "\n").replace('\r', '\n')
                assertEquals("$dir/${file.name}", text, parse(text).text)
            }
        }
    }

    private fun findRule(root: PsiElement, name: String): PsiElement? {
        val wanted = "REQNROLL_FEATURE_RULE_$name"
        if (root.node.elementType.toString() == wanted) return root
        for (child in root.children) findRule(child, name)?.let { return it }
        return null
    }
}
