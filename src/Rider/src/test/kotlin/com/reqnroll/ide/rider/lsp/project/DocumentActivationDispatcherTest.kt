package com.reqnroll.ide.rider.lsp.project

import kotlin.test.Test
import kotlin.test.assertEquals

/** Issue #986: the documentActivated notification must wait for the server to be Running. */
class DocumentActivationDispatcherTest {
    private class Harness {
        val sent = mutableListOf<String>()
        private val deferred = mutableListOf<() -> Unit>()
        var running = false
        val dispatcher = DocumentActivationDispatcher(
            DocumentActivationState(),
            whenRunning = { action -> if (running) action() else deferred.add(action) },
            send = { sent.add(it) },
        )

        fun becomeRunning() {
            running = true
            deferred.toList().also { deferred.clear() }.forEach { it() }
        }
    }

    @Test
    fun `activation while the server is not running is not sent until it is running`() {
        val h = Harness()
        h.dispatcher.onFileOpened("a.feature")
        h.dispatcher.onFileSelected("a.feature")

        assertEquals(emptyList(), h.sent)

        h.becomeRunning()

        assertEquals(listOf("a.feature"), h.sent)
    }

    @Test
    fun `restored tab activated before open is sent once after the server is running`() {
        val h = Harness()
        h.dispatcher.onFileSelected("a.feature")
        h.dispatcher.onFileOpened("a.feature")
        assertEquals(emptyList(), h.sent)

        h.becomeRunning()
        h.dispatcher.onFileSelected("a.feature")

        assertEquals(listOf("a.feature"), h.sent)
    }

    @Test
    fun `activation with a running server is sent immediately`() {
        val h = Harness()
        h.becomeRunning()
        h.dispatcher.onFileOpened("a.feature")
        h.dispatcher.onFileSelected("a.feature")

        assertEquals(listOf("a.feature"), h.sent)
    }

    @Test
    fun `file closed while the send is deferred is not sent`() {
        val h = Harness()
        h.dispatcher.onFileOpened("a.feature")
        h.dispatcher.onFileSelected("a.feature")
        h.dispatcher.onFileClosed("a.feature")

        h.becomeRunning()

        assertEquals(emptyList(), h.sent)
    }

    @Test
    fun `file closed and reopened while deferred sends only for the new open-lifetime`() {
        val h = Harness()
        h.dispatcher.onFileOpened("a.feature")
        h.dispatcher.onFileSelected("a.feature")
        h.dispatcher.onFileClosed("a.feature")
        h.dispatcher.onFileOpened("a.feature")
        h.dispatcher.onFileSelected("a.feature")

        h.becomeRunning()

        assertEquals(listOf("a.feature"), h.sent)
    }
}
