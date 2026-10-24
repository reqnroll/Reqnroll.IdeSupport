package net.reqnroll.idesupport.rider.lsp

import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.process.ProcessOutputTypes
import java.io.OutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollServerProcessTrackerTest {
    private class FakeProcessHandler : ProcessHandler() {
        fun die(exitCode: Int, vararg stderr: String) {
            stderr.forEach { notifyTextAvailable(it, ProcessOutputTypes.STDERR) }
            notifyProcessTerminated(exitCode)
        }

        override fun destroyProcessImpl() = notifyProcessTerminated(0)
        override fun detachProcessImpl() = notifyProcessDetached()
        override fun detachIsDefault() = false
        override fun getProcessInput(): OutputStream? = null
    }

    private var now = 1_000L
    private var id = 0
    private val sut = ReqnrollServerProcessTracker(now = { now }, newId = { "id-${++id}" })

    @Test
    fun `each start gets a fresh session id`() {
        assertEquals("id-1", sut.onStarting())
        assertEquals("id-2", sut.onStarting())
        assertEquals("id-2", sut.sessionId)
        assertEquals(mapOf<String, Any?>("ServerSessionId" to "id-2"), sut.startDetails())
    }

    @Test
    fun `an exit reports the exit code, uptime and the parsed crash report`() {
        val handler = FakeProcessHandler()
        sut.onStarting()
        sut.onProcessStarted(handler)
        handler.startNotify()
        now += 1_500

        handler.die(
            -532462766,
            "Unhandled exception. System.InvalidOperationException: boom\n",
            "   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line 7\n",
        )

        assertEquals(
            mapOf<String, Any?>(
                "ServerSessionId" to "id-1",
                "ExitCode" to -532462766,
                "UptimeMs" to 1_500L,
                "CrashKind" to "UnhandledException",
                "ExceptionType" to "System.InvalidOperationException",
                "Message" to "boom",
                "StackFrames" to "Reqnroll.IdeSupport.X.Y.Run:7",
            ),
            sut.describeExit(2_000),
        )
    }

    @Test
    fun `a process that never terminates is reported with what is known after the timeout`() {
        sut.onStarting()
        sut.onProcessStarted(FakeProcessHandler())

        val details = sut.describeExit(10)

        assertEquals("id-1", details["ServerSessionId"])
        assertFalse(details.containsKey("ExitCode"))
    }

    @Test
    fun `stdout is not mistaken for a crash report`() {
        val handler = FakeProcessHandler()
        sut.onStarting()
        sut.onProcessStarted(handler)
        handler.startNotify()

        handler.notifyTextAvailable("Unhandled exception. System.Exception: not stderr\n", ProcessOutputTypes.STDOUT)
        handler.die(1)

        assertFalse(sut.describeExit(2_000).containsKey("CrashKind"))
    }

    @Test
    fun `an exit describer keeps describing the run it was taken for after a restart`() {
        val handler = FakeProcessHandler()
        sut.onStarting()
        sut.onProcessStarted(handler)
        handler.startNotify()
        handler.die(-1073741571)

        val describeDeadRun = sut.exitDescriber()
        sut.onStarting()

        val details = describeDeadRun(2_000)
        assertEquals("id-1", details["ServerSessionId"])
        assertEquals(-1073741571, details["ExitCode"])
    }

    @Test
    fun `before any start there is nothing to describe`() {
        assertTrue(sut.describeExit(10).isEmpty())
        assertTrue(sut.startDetails().isEmpty())
    }
}
