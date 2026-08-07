package com.reqnroll.ide.rider.testrunner

import org.w3c.dom.Element
import java.io.ByteArrayInputStream
import java.nio.charset.StandardCharsets
import javax.xml.parsers.DocumentBuilderFactory

/** One `<UnitTestResult>` entry from a TRX file's `<Results>` section. */
data class TrxUnitTestResult(
    val testName: String,
    val outcome: String,
    val stdOut: String,
    val errorMessage: String?,
)

/**
 * Minimal TRX (`.trx`, VSTest's XML result format) extractor — only pulls what [RunTestRunner]
 * needs (`outcome`, `testName`, captured `StdOut`, `<ErrorInfo><Message>`), using the JVM's
 * built-in DOM parser (`javax.xml.parsers.DocumentBuilderFactory`). Contrast VS Code's
 * `trxParser.ts`, which hand-rolls a regex extractor because no XML-parser dependency existed in
 * that extension — the JVM ships a real one, so there's no reason not to use it here.
 */
object TrxParser {
    /** Parses every `<UnitTestResult>` entry out of a TRX file's raw text. Returns an empty list (never throws) if the XML can't be parsed at all. */
    fun parse(trxXml: String): List<TrxUnitTestResult> {
        return try {
            val factory = DocumentBuilderFactory.newInstance()
            val document = factory.newDocumentBuilder()
                .parse(ByteArrayInputStream(trxXml.toByteArray(StandardCharsets.UTF_8)))
            val nodes = document.getElementsByTagName("UnitTestResult")

            (0 until nodes.length).mapNotNull { index ->
                val element = nodes.item(index) as? Element ?: return@mapNotNull null
                TrxUnitTestResult(
                    testName = element.getAttribute("testName"),
                    outcome = element.getAttribute("outcome"),
                    stdOut = firstChildText(element, "StdOut") ?: "",
                    errorMessage = firstChildText(element, "Message"),
                )
            }
        } catch (ex: Exception) {
            emptyList()
        }
    }

    /**
     * Finds the first descendant element named [tagName] within [element] and returns its text
     * content, or null if absent. `internal` (rather than private) so it's directly unit-testable.
     */
    internal fun firstChildText(element: Element, tagName: String): String? {
        val nodes = element.getElementsByTagName(tagName)
        if (nodes.length == 0) return null
        return nodes.item(0).textContent
    }
}
