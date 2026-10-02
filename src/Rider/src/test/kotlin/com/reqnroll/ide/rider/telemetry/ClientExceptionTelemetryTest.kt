package com.reqnroll.ide.rider.telemetry

import com.intellij.openapi.progress.ProcessCanceledException
import java.io.IOException
import java.util.concurrent.CancellationException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ClientExceptionTelemetryTest {
    private class Recorder {
        val sent = mutableListOf<Pair<String, Map<String, String>>>()
        val reporter = ClientExceptionTelemetry.Reporter { name, props -> sent.add(name to props) }
    }

    @Test
    fun `uses the server UnhandledException schema plus ExceptionOrigin Client`() {
        val props = ClientExceptionTelemetry.buildProperties(IOException("disk gone"))

        assertEquals("java.io.IOException", props["ExceptionType"])
        assertEquals("disk gone", props["Message"])
        assertEquals("Client", props["ExceptionOrigin"])
        assertEquals("UnhandledException", RiderTelemetryTransmitter.UNHANDLED_EXCEPTION)
    }

    @Test
    fun `never carries a stack trace`() {
        val props = ClientExceptionTelemetry.buildProperties(IllegalStateException("x"))

        assertTrue(props.keys.all { it in setOf("ExceptionType", "Message", "ExceptionOrigin", "Source") })
        assertTrue(props.values.none { it.contains("\tat ") || it.contains(".kt:") })
    }

    @Test
    fun `redacts Windows UNC and POSIX paths`() {
        val text = ClientExceptionTelemetry.redactPaths(
            """ENOENT C:\Users\bob\proj\a.feature and \\srv\share\x and /home/bob/proj/b.cs failed""",
        )

        assertEquals("ENOENT <path> and <path> and <path> failed", text)
    }

    @Test
    fun `scrubs paths and caps the length of the message`() {
        val props = ClientExceptionTelemetry.buildProperties(
            IOException("cannot open /home/bob/secret.feature " + "x".repeat(2000)),
        )

        assertFalse(props.getValue("Message").contains("bob"))
        assertTrue(props.getValue("Message").startsWith("cannot open <path>"))
        assertTrue(props.getValue("Message").length <= ClientExceptionTelemetry.MAX_MESSAGE_LENGTH)
    }

    @Test
    fun `a null message is reported as an empty string`() {
        assertEquals("", ClientExceptionTelemetry.buildProperties(IllegalStateException()).getValue("Message"))
    }

    @Test
    fun `Source is the simple name of the topmost plugin class and omitted when none`() {
        // This test class lives in com.reqnroll.ide.rider.telemetry, so its own frame is skipped as a
        // helper frame; the same exception thrown from a plugin class elsewhere would be attributed to it.
        assertNull(ClientExceptionTelemetry.resolveSource(IllegalStateException("x")))

        val fromPlugin = IllegalStateException("x").apply {
            stackTrace = arrayOf(
                StackTraceElement("java.util.ArrayList", "get", "ArrayList.java", 1),
                StackTraceElement("com.reqnroll.ide.rider.lsp.ReqnrollRequestSender\$findStepUsages\$1", "invoke", "x.kt", 1),
                StackTraceElement("com.reqnroll.ide.rider.actions.FindStepUsagesRunner", "run", "y.kt", 2),
            )
        }
        assertEquals("ReqnrollRequestSender", ClientExceptionTelemetry.resolveSource(fromPlugin))
        assertEquals("ReqnrollRequestSender", ClientExceptionTelemetry.buildProperties(fromPlugin)["Source"])
    }

    @Test
    fun `sends an identical exception only once`() {
        val r = Recorder()

        assertTrue(r.reporter.report(IOException("same")))
        assertFalse(r.reporter.report(IOException("same")))

        assertEquals(1, r.sent.size)
        assertEquals("UnhandledException", r.sent[0].first)
    }

    @Test
    fun `treats a different message or type as distinct`() {
        val r = Recorder()

        r.reporter.report(IOException("a"))
        r.reporter.report(IOException("b"))
        r.reporter.report(IllegalStateException("a"))

        assertEquals(3, r.sent.size)
    }

    @Test
    fun `caps the total events per session`() {
        val r = Recorder()

        repeat(ClientExceptionTelemetry.MAX_EVENTS_PER_SESSION + 10) { r.reporter.report(IOException("unique $it")) }

        assertEquals(ClientExceptionTelemetry.MAX_EVENTS_PER_SESSION, r.sent.size)
    }

    @Test
    fun `does not report cancellations`() {
        val r = Recorder()

        assertFalse(r.reporter.report(ProcessCanceledException()))
        assertFalse(r.reporter.report(CancellationException("cancelled")))

        assertEquals(0, r.sent.size)
    }

    @Test
    fun `never throws even when the sender throws`() {
        val reporter = ClientExceptionTelemetry.Reporter { _, _ -> throw IllegalStateException("transport down") }

        assertFalse(reporter.report(IOException("x")))
    }

    @Test
    fun `a report raised while reporting does not recurse`() {
        lateinit var reporter: ClientExceptionTelemetry.Reporter
        var nested: Boolean? = null
        reporter = ClientExceptionTelemetry.Reporter { _, _ -> nested = reporter.report(IOException("inner")) }

        assertTrue(reporter.report(IOException("outer")))

        assertEquals(false, nested)
    }
}
