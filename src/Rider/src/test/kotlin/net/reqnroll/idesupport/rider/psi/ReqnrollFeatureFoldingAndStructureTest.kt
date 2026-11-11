package net.reqnroll.idesupport.rider.psi

import com.intellij.psi.PsiElement
import com.intellij.testFramework.ParsingTestCase

/** Folding regions and the Structure view tree, taken from a real parse of feature text. */
class ReqnrollFeatureFoldingAndStructureTest : ParsingTestCase("", "feature", ReqnrollFeatureParserDefinition()) {
    override fun getTestDataPath(): String = "."

    override fun skipSpaces(): Boolean = true

    override fun includeRanges(): Boolean = false

    /** The folded text of each region: from the end of its first line to the end of its content. */
    private fun folds(text: String): List<String> {
        val file = parseFile("test.feature", text)
        return ReqnrollFeatureFoldingBuilder.foldRegions(file.node, text).map { text.substring(it.range.startOffset, it.range.endOffset) }
    }

    private fun lines(vararg lines: String) = lines.joinToString("\n", postfix = "\n")

    fun `test a scenario folds from the end of its keyword line`() {
        val text = lines("Feature: f", "  Scenario: s", "    Given a", "    When b")

        assertTrue(folds(text).toString(), "\n    Given a\n    When b" in folds(text))
    }

    fun `test the feature folds from its keyword line, not from a tag line above it`() {
        val text = lines("@smoke", "Feature: f", "  Scenario: s", "    Given a")

        val feature = folds(text).first()

        assertFalse(feature.contains("@smoke"))
        assertTrue(feature.startsWith("\n  Scenario: s"))
    }

    fun `test doc strings, tables and examples fold`() {
        val text = lines(
            "Feature: f",
            "  Scenario Outline: s",
            "    Given a",
            "      \"\"\"",
            "      body",
            "      \"\"\"",
            "    When b",
            "      | h |",
            "      | 1 |",
            "    Examples:",
            "      | n |",
            "      | 1 |",
        )

        val folded = folds(text)

        assertTrue(folded.toString(), folded.any { it == "\n      body\n      \"\"\"" })
        assertTrue(folded.toString(), folded.any { it == "\n      | 1 |" })
        assertTrue(folded.toString(), folded.any { it == "\n      | n |\n      | 1 |" })
    }

    fun `test a lone header does not fold and a scenario with no steps adds no fold of its own`() {
        assertEquals(emptyList<String>(), folds(lines("Feature: f")))
        // Only the Feature folds (its one body line); the one-line Scenario has nothing below its header.
        assertEquals(listOf("\n  Scenario: s"), folds(lines("Feature: f", "  Scenario: s")))
    }

    fun `test regions are ordered outer before inner and cover only text inside the file`() {
        val text = lines("Feature: f", "  Scenario: s", "    Given a", "    When b")
        val file = parseFile("test.feature", text)

        val regions = ReqnrollFeatureFoldingBuilder.foldRegions(file.node, text)

        assertTrue(regions.size >= 2)
        assertTrue(regions.zipWithNext().all { (a, b) -> a.range.startOffset <= b.range.startOffset })
        assertTrue(regions.all { it.range.endOffset <= text.length && it.range.length > 0 })
    }

    /**
     * The Structure view's text tree, one indented line per node. Built from the same functions the view's elements
     * use; the elements themselves need platform services that a parsing test does not start.
     */
    private fun structure(file: PsiElement): List<String> {
        val out = ArrayList<String>()
        fun visit(node: com.intellij.lang.ASTNode, depth: Int) {
            out.add("  ".repeat(depth) + ReqnrollFeatureStructure.headerText(node))
            ReqnrollFeatureStructure.childBlocks(node).forEach { visit(it, depth + 1) }
        }
        ReqnrollFeatureStructure.childBlocks(file.node).forEach { visit(it, 0) }
        return out
    }

    fun `test the structure nests steps in scenarios in the feature`() {
        val file = parseFile(
            "test.feature",
            lines(
                "@smoke", "Feature: Eating", "  Background:", "    Given a fridge",
                "  Rule: Fruit", "    Scenario: Apple", "      Given an apple", "      When I eat it",
                "  Scenario Outline: Many", "    Given <n> things", "    Examples:", "      | n |", "      | 1 |",
            ),
        )

        assertEquals(
            listOf(
                "Feature: Eating",
                "  Background:",
                "    Given a fridge",
                "  Rule: Fruit",
                "    Scenario: Apple",
                "      Given an apple",
                "      When I eat it",
                // Gherkin's grammar, not indentation, decides nesting: a Rule holds every scenario after it.
                "    Scenario Outline: Many",
                "      Given <n> things",
                "      Examples:",
            ),
            structure(file),
        )
    }

    fun `test an empty file has an empty structure`() {
        assertEquals(emptyList<String>(), structure(parseFile("test.feature", "")))
    }

    fun `test every corpus file yields folds and a structure without failing`() {
        for (dir in listOf("good", "bad")) {
            val folder = java.io.File(javaClass.getResource("/gherkin-corpus/$dir")!!.toURI())
            for (feature in folder.listFiles { f -> f.extension == "feature" }!!.sortedBy { it.name }) {
                val text = feature.readText().replace("\r\n", "\n").replace('\r', '\n')
                val file = parseFile("test.feature", text)
                val regions = ReqnrollFeatureFoldingBuilder.foldRegions(file.node, text)
                assertTrue("$dir/${feature.name}", regions.all { it.range.startOffset in 0..text.length && it.range.endOffset <= text.length })
                structure(file)
            }
        }
    }
}
