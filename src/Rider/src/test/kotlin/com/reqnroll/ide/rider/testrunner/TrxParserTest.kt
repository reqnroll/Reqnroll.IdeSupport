package com.reqnroll.ide.rider.testrunner

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TrxParserTest {
    // Deliberately not using trimIndent() here: resultsXml is itself the (already-flush-left)
    // output of another trimIndent() call, so splicing it into an indented outer template and
    // trimIndent()-ing the result computes a common indentation of zero (from resultsXml's own
    // lines) and leaves this template's own leading whitespace in place — including before
    // "<?xml ...?>", which XML parsers reject outright ("Content is not allowed in prolog"). This
    // template is written flush-left from the start instead, so no indentation stripping is needed.
    private fun trxWithResults(resultsXml: String): String = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    $resultsXml
  </Results>
</TestRun>"""

    @Test
    fun `parses a single passing result`() {
        val trx = trxWithResults(
            """
            <UnitTestResult testId="1" testName="AddTwoNumbers" outcome="Passed">
              <Output><StdOut>Given a passing step
            -&gt; done: Step() (0.0s)</StdOut></Output>
            </UnitTestResult>
            """.trimIndent(),
        )

        val results = TrxParser.parse(trx)

        assertEquals(1, results.size)
        assertEquals("AddTwoNumbers", results[0].testName)
        assertEquals("Passed", results[0].outcome)
        assertTrue(results[0].stdOut.contains("-> done: Step() (0.0s)"))
        assertNull(results[0].errorMessage)
    }

    @Test
    fun `parses a failing result with ErrorInfo`() {
        val trx = trxWithResults(
            """
            <UnitTestResult testId="1" testName="AddTwoNumbers" outcome="Failed">
              <Output>
                <StdOut>Given a step
            -&gt; error: deliberate failure (0.0s)</StdOut>
                <ErrorInfo>
                  <Message>deliberate failure</Message>
                  <StackTrace>at Foo.feature:line 3</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            """.trimIndent(),
        )

        val results = TrxParser.parse(trx)

        assertEquals("Failed", results[0].outcome)
        assertEquals("deliberate failure", results[0].errorMessage)
    }

    @Test
    fun `parses multiple UnitTestResult entries for a row-tests method`() {
        val trx = trxWithResults(
            """
            <UnitTestResult testId="1" testName="AddNumbers (a: &quot;1&quot;)" outcome="Passed">
              <Output><StdOut>done</StdOut></Output>
            </UnitTestResult>
            <UnitTestResult testId="2" testName="AddNumbers (a: &quot;2&quot;)" outcome="Failed">
              <Output><StdOut>failed</StdOut></Output>
            </UnitTestResult>
            """.trimIndent(),
        )

        val results = TrxParser.parse(trx)

        assertEquals(2, results.size)
        assertEquals("Passed", results[0].outcome)
        assertEquals("Failed", results[1].outcome)
    }

    @Test
    fun `handles a self-closing UnitTestResult with no Output`() {
        val trx = trxWithResults("""<UnitTestResult testId="1" testName="Empty" outcome="NotExecuted" />""")

        val results = TrxParser.parse(trx)

        assertEquals(1, results.size)
        assertEquals("", results[0].stdOut)
        assertNull(results[0].errorMessage)
    }

    @Test
    fun `returns an empty list for a TRX with no results`() {
        assertEquals(emptyList(), TrxParser.parse(trxWithResults("")))
    }

    @Test
    fun `returns an empty list for unparsable XML instead of throwing`() {
        assertEquals(emptyList(), TrxParser.parse("not xml at all <<<"))
    }
}
