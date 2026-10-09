package net.reqnroll.idesupport.rider.telemetry

import com.intellij.openapi.progress.ProcessCanceledException
import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import org.eclipse.lsp4j.jsonrpc.messages.ResponseError
import java.io.IOException
import java.util.concurrent.ExecutionException
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
        val text = ClientExceptionTelemetry.scrubMessage(
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
        // This test class lives in net.reqnroll.idesupport.rider.telemetry, so its own frame is skipped as a
        // helper frame; the same exception thrown from a plugin class elsewhere would be attributed to it.
        assertNull(ClientExceptionTelemetry.resolveSource(IllegalStateException("x")))

        val fromPlugin = IllegalStateException("x").apply {
            stackTrace = arrayOf(
                StackTraceElement("java.util.ArrayList", "get", "ArrayList.java", 1),
                StackTraceElement("net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender\$findStepUsages\$1", "invoke", "x.kt", 1),
                StackTraceElement("net.reqnroll.idesupport.rider.actions.FindStepUsagesRunner", "run", "y.kt", 2),
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

    private fun scrub(text: String) = ClientExceptionTelemetry.scrubMessage(text)

    @Test
    fun `redacts a drive-letter path whose user directory contains spaces`() {
        val text = scrub("""EACCES C:\Users\John Smith\My Projects\Calc\a.feature denied""")

        assertEquals("EACCES <path> denied", text)
    }

    @Test
    fun `redacts UNC and POSIX paths containing spaces`() {
        assertEquals("<path> failed", scrub("""\\srv\my share\dir\f failed"""))
        assertEquals("open <path> now", scrub("open /home/john smith/work/x now"))
    }

    @Test
    fun `redacts a quoted path wholly`() {
        val text = scrub("""Cannot find "C:\Users\Jane Doe\proj\x.csproj" or '/home/jane doe/x'""")

        assertFalse(text.contains("Jane"), text)
        assertFalse(text.contains("proj"), text)
    }

    @Test
    fun `drops a URL wholly so a token in its query string or fragment never leaves`() {
        val text = scrub("fetch https://example.com/a/b?token=SECRET123&x=1#frag failed; file:///c:/Users/bob/x.json too")

        assertEquals("fetch <url> failed; <url> too", text)
    }

    @Test
    fun `redacts relative paths, bare source file names and home-style paths`() {
        assertEquals("missing <path> and <path>", scrub("missing src/Features/Calc.feature and ../shared/x"))
        assertEquals("Error in <path>, <path> and <path>", scrub("Error in Calculator.feature, Steps.cs and App.csproj"))
        assertEquals("x <path> here", scrub("""x %USERPROFILE%\proj\a here"""))
        assertEquals("x <path> here", scrub("x ~/work/proj/a here"))
        assertEquals("x <path> here", scrub("x \$HOME/work/a here"))
    }

    @Test
    fun `two paths in one message are both redacted without swallowing the words between them`() {
        assertEquals(
            "ENOENT <path> and <path> and <path> failed",
            scrub("""ENOENT C:\Users\bob\a.feature and \\srv\share\x and /home/bob/b.cs failed"""),
        )
    }

    @Test
    fun `leaves ordinary words, method-looking words and dotted type names alone`() {
        val text = "and/or textDocument/definition System.Text.Json x"

        assertEquals(text, scrub(text))
    }

    @Test
    fun `drops the quoted source snippet of a parser-style message`() {
        val props = ClientExceptionTelemetry.buildProperties(
            IllegalStateException("""Unexpected token } in "{"stepText": "Given my secret password is hunter2", }" at 5"""),
        )

        assertFalse(props.getValue("Message").contains("hunter2"))
        assertFalse(props.getValue("Message").contains("secret"))
    }

    @Test
    fun `does not report an LSP ResponseErrorException, bare or wrapped`() {
        val r = Recorder()
        val responseError = ResponseErrorException(ResponseError(-32603, "No step matches 'Given my secret'", null))

        assertFalse(r.reporter.report(responseError))
        assertFalse(r.reporter.report(ExecutionException(responseError)))
        assertFalse(r.reporter.report(RuntimeException("wrapper", RuntimeException("deeper", responseError))))

        assertEquals(0, r.sent.size)
    }

    @Test
    fun `counts duplicates and attaches SuppressedCount to the next sent event`() {
        val r = Recorder()

        r.reporter.report(IOException("a"))
        r.reporter.report(IOException("a"))
        r.reporter.report(IOException("a"))
        r.reporter.report(IOException("b"))
        r.reporter.report(IOException("c"))

        assertNull(r.sent[0].second["SuppressedCount"])
        assertEquals("2", r.sent[1].second["SuppressedCount"])
        assertNull(r.sent[2].second["SuppressedCount"])
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
