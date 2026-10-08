package com.reqnroll.ide.rider.taglinks

import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter
import org.eclipse.lsp4j.DocumentLink
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TagLinkSupportTest {
    // "Feature: F\n@issue:1234 @smoke\nScenario: S\n": line starts at 0, 11, 30
    private val lineStarts = listOf(0, 11, 30)
    private val documentLength = 42

    private fun link(line: Int, startChar: Int, endChar: Int, target: String?) =
        DocumentLink(Range(Position(line, startChar), Position(line, endChar)), target)

    private fun ranges(vararg links: DocumentLink) =
        TagLinkSupport.toRanges(links.toList(), documentLength) { lineStarts.getOrNull(it) }

    @Test
    fun `isOpenableUrl allows only http and https`() {
        assertTrue(TagLinkSupport.isOpenableUrl("https://example.com/issues/1"))
        assertTrue(TagLinkSupport.isOpenableUrl("HTTP://example.com/issues/1"))
        assertFalse(TagLinkSupport.isOpenableUrl("file:///C:/Windows/System32/calc.exe"))
        assertFalse(TagLinkSupport.isOpenableUrl("javascript:alert(1)"))
        assertFalse(TagLinkSupport.isOpenableUrl("not a url"))
        assertFalse(TagLinkSupport.isOpenableUrl(""))
        assertFalse(TagLinkSupport.isOpenableUrl(null))
    }

    @Test
    fun `toRanges maps line and character positions to document offsets`() {
        val result = ranges(link(1, 0, 11, "https://example.com/1234"))

        assertEquals(listOf(TagLinkRange(11, 22, "https://example.com/1234")), result)
    }

    @Test
    fun `toRanges drops non-openable targets and out-of-range positions`() {
        val result = ranges(
            link(1, 0, 11, "file:///C:/secret.txt"),
            link(1, 0, 11, null),
            link(9, 0, 3, "https://example.com/stale-line"),
            link(1, 5, 5, "https://example.com/empty"),
            link(2, 0, 99, "https://example.com/past-the-end"),
        )

        assertTrue(result.isEmpty())
    }

    @Test
    fun `hoverHtml shows the target and the gesture that follows it`() {
        assertEquals(
            "<html>https://example.com/issues/1234<br>Ctrl + click to follow link</html>",
            TagLinkSupport.hoverHtml("https://example.com/issues/1234", "Ctrl"),
        )
        assertTrue(TagLinkSupport.hoverHtml("https://example.com/", "Cmd").contains("Cmd + click"))
    }

    @Test
    fun `hoverHtml escapes markup in the target`() {
        val html = TagLinkSupport.hoverHtml("https://example.com/?a=1&b=<script>\"x\"</script>", "Ctrl")

        assertFalse(html.contains("<script>"))
        assertTrue(html.contains("a=1&amp;b=&lt;script&gt;&quot;x&quot;&lt;/script&gt;"))
    }

    @Test
    fun `linkAt is start inclusive and end exclusive`() {
        val links = listOf(TagLinkRange(11, 22, "https://example.com/1"))

        assertEquals(links[0], TagLinkSupport.linkAt(links, 11))
        assertEquals(links[0], TagLinkSupport.linkAt(links, 21))
        assertNull(TagLinkSupport.linkAt(links, 22))
        assertNull(TagLinkSupport.linkAt(links, 10))
    }

    @Test
    fun `telemetry event name is the catalog constant`() {
        assertEquals("TagLink command executed", RiderTelemetryTransmitter.TAG_LINK_COMMAND_EXECUTED)
    }
}
