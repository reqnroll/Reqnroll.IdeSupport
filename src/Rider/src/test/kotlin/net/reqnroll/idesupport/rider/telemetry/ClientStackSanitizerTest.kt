package net.reqnroll.idesupport.rider.telemetry

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ClientStackSanitizerTest {
    private fun frame(className: String, method: String, line: Int = 10, file: String = "Secret.kt") =
        StackTraceElement(className, method, file, line)

    @Test
    fun `names plugin frames as package Class method line and collapses foreign runs`() {
        val result = ClientStackSanitizer.sanitize(
            arrayOf(
                frame("java.util.ArrayList", "get"),
                frame("com.intellij.openapi.Foo", "bar"),
                frame("net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender", "send", 42),
                frame("kotlinx.coroutines.DispatchedTask", "run"),
            ),
        )

        assertEquals(
            "[external]\nnet.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender.send:42\n[external]",
            result,
        )
    }

    @Test
    fun `never emits file names`() {
        val result = ClientStackSanitizer.sanitize(
            arrayOf(frame("net.reqnroll.idesupport.rider.actions.Runner", "run", 7, "C:\\Users\\bob\\Secret.kt")),
        )!!

        assertTrue(!result.contains("Secret") && !result.contains("bob") && !result.contains(".kt"))
    }

    @Test
    fun `folds lambda and inner classes into the declaring class and member`() {
        val result = ClientStackSanitizer.sanitize(
            arrayOf(frame("net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender\$findStepUsages\$1", "invoke", 5)),
        )

        assertEquals("net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender.findStepUsages{lambda}:5", result)
    }

    @Test
    fun `skips the plugins telemetry and logging helper frames`() {
        val result = ClientStackSanitizer.sanitize(
            arrayOf(
                frame("net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger", "warn"),
                frame("net.reqnroll.idesupport.rider.telemetry.ClientExceptionTelemetry", "report"),
                frame("net.reqnroll.idesupport.rider.actions.Runner", "run", 3),
            ),
        )

        assertEquals("net.reqnroll.idesupport.rider.actions.Runner.run:3", result)
    }

    @Test
    fun `a lookalike package is not the plugin and no plugin frame yields null`() {
        assertNull(ClientStackSanitizer.sanitize(arrayOf(frame("net.reqnroll.idesupport.riderevil.X", "y"))))
        assertNull(ClientStackSanitizer.sanitize(arrayOf(frame("java.lang.Thread", "run"))))
        assertNull(ClientStackSanitizer.sanitize(emptyArray()))
    }

    @Test
    fun `is bounded in entries and length`() {
        val frames = Array(50) { frame("net.reqnroll.idesupport.rider.actions.Runner$it", "run", it + 1) }

        val result = ClientStackSanitizer.sanitize(frames)!!

        assertEquals(ClientStackSanitizer.MAX_FRAMES, result.split('\n').size)
        assertTrue(result.length <= ClientStackSanitizer.MAX_LENGTH)
    }
}
