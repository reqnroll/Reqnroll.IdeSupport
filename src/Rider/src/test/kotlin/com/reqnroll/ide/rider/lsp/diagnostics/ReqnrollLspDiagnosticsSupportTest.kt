package com.reqnroll.ide.rider.lsp.diagnostics

import org.eclipse.lsp4j.Diagnostic
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollLspDiagnosticsSupportTest {
    private val support = ReqnrollLspDiagnosticsSupport()

    private fun tooltipFor(message: String): String =
        support.getTooltip(Diagnostic().apply { this.message = message })

    @Test
    fun `single-line message renders as one html line`() {
        assertEquals("<html><nobr>Step definition not found.</nobr></html>", tooltipFor("Step definition not found."))
    }

    // Regression test for #176: the platform default forwards Diagnostic.message verbatim into a
    // tooltip that Rider renders as HTML, where a bare '\n' collapses like ordinary whitespace — so
    // every candidate merged onto the header's line instead of getting its own row.
    @Test
    fun `each newline-separated line gets its own html row`() {
        val message = "Ambiguous steps:\nN.Steps.First()\nN.Steps.Second()"

        assertEquals(
            "<html><nobr>Ambiguous steps:</nobr><br><nobr>N.Steps.First()</nobr><br><nobr>N.Steps.Second()</nobr></html>",
            tooltipFor(message),
        )
    }

    @Test
    fun `CRLF and lone CR line endings are also split`() {
        assertEquals(
            "<html><nobr>a</nobr><br><nobr>b</nobr><br><nobr>c</nobr></html>",
            tooltipFor("a\r\nb\rc"),
        )
    }

    @Test
    fun `html-significant characters in a candidate line are escaped`() {
        assertEquals(
            "<html><nobr>N.Steps.Add(int &lt;a&gt;, int &lt;b&gt;)</nobr></html>",
            tooltipFor("N.Steps.Add(int <a>, int <b>)"),
        )
    }

    // Documents current behavior for an empty diagnostic message (not necessarily a fix): splitting
    // "" on the line-ending regex yields a single empty segment, so XmlStringUtil.wrapInHtmlLines
    // still renders one (empty) <nobr> element rather than an empty <html></html>.
    @Test
    fun `empty message renders as a single empty html line`() {
        assertEquals("<html><nobr></nobr></html>", tooltipFor(""))
    }
}
