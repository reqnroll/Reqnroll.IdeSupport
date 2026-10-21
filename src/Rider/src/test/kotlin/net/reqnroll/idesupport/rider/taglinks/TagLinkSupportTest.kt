package net.reqnroll.idesupport.rider.taglinks

import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
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
    fun `isOpenableUrl allows any scheme except the blocked ones`() {
        assertTrue(TagLinkSupport.isOpenableUrl("https://example.com/issues/1"))
        assertTrue(TagLinkSupport.isOpenableUrl("HTTP://example.com/issues/1"))
        assertTrue(TagLinkSupport.isOpenableUrl("file:///C:/specs/story.html"))
        assertTrue(TagLinkSupport.isOpenableUrl("jira://browse/PROJ-1"))
        assertTrue(TagLinkSupport.isOpenableUrl("vscode://file/C:/repo/a.cs"))
        assertTrue(TagLinkSupport.isOpenableUrl("slack://channel?id=C1"))
        assertFalse(TagLinkSupport.isOpenableUrl("javascript:alert(1)"))
        assertFalse(TagLinkSupport.isOpenableUrl("JavaScript:alert(1)"))
        assertFalse(TagLinkSupport.isOpenableUrl("vbscript:msgbox(1)"))
        assertFalse(TagLinkSupport.isOpenableUrl("data:text/html,<b>x</b>"))
        assertFalse(TagLinkSupport.isOpenableUrl("command:workbench.action.reloadWindow"))
        assertFalse(TagLinkSupport.isOpenableUrl("relative/path"))
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
            link(1, 0, 11, "javascript:alert(1)"),
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
        assertEquals("Command.TagLink", RiderTelemetryTransmitter.TAG_LINK_COMMAND_EXECUTED)
    }
}
