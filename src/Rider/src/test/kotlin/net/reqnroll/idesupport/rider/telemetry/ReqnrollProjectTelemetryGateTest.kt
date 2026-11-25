package net.reqnroll.idesupport.rider.telemetry

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReqnrollProjectTelemetryGateTest {
    private val sut = ReqnrollProjectTelemetryGate()
    private val sent = mutableListOf<String>()

    private fun send(eventName: String) = sut.send(eventName) { sent.add(eventName) }

    @Test
    fun `holds ordinary events until a Reqnroll project is reported`() {
        send("Lifecycle.Extension.Loaded")
        send("Server.Session.Started")

        assertTrue(sent.isEmpty())
        assertFalse(sut.isOpen)
    }

    @Test
    fun `opening sends the held events in order, then later events directly`() {
        send("Lifecycle.Extension.Loaded")
        send("Server.Session.Started")

        sut.open()
        send("Project.Opened")

        assertEquals(listOf("Lifecycle.Extension.Loaded", "Server.Session.Started", "Project.Opened"), sent)
        assertTrue(sut.isOpen)
    }

    @Test
    fun `opening twice sends the held events once`() {
        send("Lifecycle.Extension.Loaded")

        sut.open()
        sut.open()

        assertEquals(1, sent.size)
    }

    @Test
    fun `errors and server start failures are never held`() {
        val alwaysSent = listOf(
            "Error.UnhandledException",
            "Server.Process.StartFailed",
            "Server.Process.ExitedUnexpectedly",
            "Server.Process.ConnectionLost",
        )
        alwaysSent.forEach(::send)

        assertEquals(alwaysSent, sent)
        assertFalse(ReqnrollProjectTelemetryGate.isAlwaysAllowed("Server.Process.Restarted"))
    }

    @Test
    fun `holds at most the cap and drops the rest`() {
        repeat(ReqnrollProjectTelemetryGate.MAX_HELD_EVENTS + 5) { send("Perf.Sample") }

        sut.open()

        assertEquals(ReqnrollProjectTelemetryGate.MAX_HELD_EVENTS, sent.size)
    }

    @Test
    fun `a failing held send does not stop the others`() {
        sut.send("Lifecycle.Extension.Loaded") { throw IllegalStateException("boom") }
        send("Server.Session.Started")

        sut.open()

        assertEquals(listOf("Server.Session.Started"), sent)
    }

    @Test
    fun `onOpened runs once after the held events, or at once when already open`() {
        send("Server.Session.Started")
        sut.onOpened { send("Lifecycle.Extension.Loaded") }
        assertTrue(sent.isEmpty())

        sut.open()
        sut.open()
        assertEquals(listOf("Server.Session.Started", "Lifecycle.Extension.Loaded"), sent)

        sut.onOpened { send("Lifecycle.Usage.Recorded") }
        assertEquals("Lifecycle.Usage.Recorded", sent.last())
    }
}
