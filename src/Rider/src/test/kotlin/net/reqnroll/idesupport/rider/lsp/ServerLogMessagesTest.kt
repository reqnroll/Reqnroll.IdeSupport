package net.reqnroll.idesupport.rider.lsp

import org.eclipse.lsp4j.MessageParams
import org.eclipse.lsp4j.MessageType
import kotlin.test.Test
import kotlin.test.assertEquals

class ServerLogMessagesTest {
    private fun written(params: MessageParams?): List<Pair<String, String>> {
        val entries = mutableListOf<Pair<String, String>>()
        ServerLogMessages.write(params) { level, message -> entries += level to message }
        return entries
    }

    @Test
    fun `writes each message type at its own level`() {
        assertEquals(listOf("Error" to "e"), written(MessageParams(MessageType.Error, "e")))
        assertEquals(listOf("Warning" to "w"), written(MessageParams(MessageType.Warning, "w")))
        assertEquals(listOf("Info" to "i"), written(MessageParams(MessageType.Info, "i")))
        assertEquals(listOf("Verbose" to "l"), written(MessageParams(MessageType.Log, "l")))
    }

    @Test
    fun `ignores a message without text`() {
        assertEquals(emptyList(), written(MessageParams(MessageType.Warning, "")))
        assertEquals(emptyList(), written(null))
    }
}
