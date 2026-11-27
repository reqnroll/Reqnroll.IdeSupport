package net.reqnroll.idesupport.rider.lsp

import net.reqnroll.idesupport.rider.lsp.protocol.CopyToClipboardParams
import kotlin.test.Test
import kotlin.test.assertEquals

class ServerClipboardTest {
    private fun copied(params: CopyToClipboardParams?): List<String> {
        val texts = mutableListOf<String>()
        ServerClipboard.copy(params) { texts += it }
        return texts
    }

    @Test
    fun `copies the text of the notification`() {
        val snippet = "    [When(\"I press add\")]\n    public void WhenIPressAdd()\n    {\n    }\n"
        assertEquals(listOf(snippet), copied(CopyToClipboardParams(snippet)))
    }

    @Test
    fun `ignores a notification without text`() {
        assertEquals(emptyList(), copied(null))
        assertEquals(emptyList(), copied(CopyToClipboardParams()))
        assertEquals(emptyList(), copied(CopyToClipboardParams("")))
    }
}
