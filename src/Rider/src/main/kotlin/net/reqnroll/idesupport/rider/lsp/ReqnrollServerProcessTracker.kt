package net.reqnroll.idesupport.rider.lsp

import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.process.ProcessListener
import com.intellij.execution.process.ProcessOutputTypes
import com.intellij.openapi.util.Key
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Follows the language-server process across restarts for the server-lifecycle telemetry: gives each start its
 * own `--server-session-id` (the server stamps it as `SessionId`, so an exit joins to the dead run's events), and
 * records how the process ended and the crash report the .NET runtime printed to its stderr. The Kotlin
 * counterpart of the VS Code extension's `ServerProcessTracker`.
 */
internal class ReqnrollServerProcessTracker(
    private val now: () -> Long = System::currentTimeMillis,
    private val newId: () -> String = { UUID.randomUUID().toString() },
) {
    private class Run(val sessionId: String, val startedAt: Long) {
        val collector = ServerCrashReportCollector()
        val terminated = CountDownLatch(1)

        @Volatile
        var exitCode: Int? = null

        @Volatile
        var exitedAt: Long? = null
    }

    @Volatile
    private var run: Run? = null

    /** The current start's id, or null before the first start. */
    val sessionId: String?
        get() = run?.sessionId

    /** A new start is about to launch the server: give it a fresh id, which the command line then reads. */
    fun onStarting(): String {
        val started = Run(newId(), now())
        run = started
        return started.sessionId
    }

    /** The server process for the current start was created: watch its stderr and termination. */
    fun onProcessStarted(handler: ProcessHandler) {
        val current = run ?: return
        handler.addProcessListener(object : ProcessListener {
            override fun onTextAvailable(event: ProcessEvent, outputType: Key<*>) {
                if (outputType == ProcessOutputTypes.STDERR) current.collector.onText(event.text)
            }

            // Called after the output readers have finished, so all of stderr has been seen by then.
            override fun processTerminated(event: ProcessEvent) {
                current.exitCode = event.exitCode
                current.exitedAt = now()
                current.terminated.countDown()
            }
        })
    }

    /** `ServerSessionId` only: for a failed start, where there is no process to describe yet. */
    fun startDetails(): Map<String, Any?> =
        run?.let { mapOf(SERVER_SESSION_ID_PROPERTY to it.sessionId) } ?: emptyMap()

    /**
     * The current run's exit details: `ServerSessionId`, `ExitCode`, `UptimeMs` and the parsed crash report.
     * Blocks up to [timeoutMs] for the process to finish terminating, because the platform can report the server
     * stopped before the process has exited and before the last of stderr (the crash report) has been read.
     * Call it off the EDT.
     */
    fun describeExit(timeoutMs: Long): Map<String, Any?> = exitDescriber()(timeoutMs)

    /**
     * [describeExit] bound to the run that is current now, for a caller that describes it later on another
     * thread: by then the platform may already have started the next run.
     */
    fun exitDescriber(): (timeoutMs: Long) -> Map<String, Any?> {
        val current = run ?: return { emptyMap() }
        return { timeoutMs -> describe(current, timeoutMs) }
    }

    private fun describe(current: Run, timeoutMs: Long): Map<String, Any?> {
        try {
            current.terminated.await(timeoutMs, TimeUnit.MILLISECONDS)
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        }

        return buildMap {
            put(SERVER_SESSION_ID_PROPERTY, current.sessionId)
            current.exitCode?.let { put(EXIT_CODE_PROPERTY, it) }
            val uptime = (current.exitedAt ?: now()) - current.startedAt
            if (uptime >= 0) put(UPTIME_MS_PROPERTY, uptime)
            current.collector.parse()?.let { putAll(it.toProperties()) }
        }
    }

    companion object {
        /** Mirrors `ServerSession.PropertyName` in `Reqnroll.IdeSupport.Common`. */
        const val SERVER_SESSION_ID_PROPERTY = "ServerSessionId"

        /** Mirrors `ServerSession.ServerArgumentName`. */
        const val SERVER_SESSION_ID_ARGUMENT = "--server-session-id"

        /** Mirror `ServerLifecycleTelemetry.ExitCodeKey` / `UptimeMsKey`. */
        const val EXIT_CODE_PROPERTY = "ExitCode"
        const val UPTIME_MS_PROPERTY = "UptimeMs"

        /** How long an unexpected exit waits for the process to finish terminating. */
        const val EXIT_DETAILS_TIMEOUT_MS = 2000L
    }
}
