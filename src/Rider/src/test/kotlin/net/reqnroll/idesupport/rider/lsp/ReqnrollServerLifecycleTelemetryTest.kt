package net.reqnroll.idesupport.rider.lsp

import com.intellij.platform.lsp.api.LspServerState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ReqnrollServerLifecycleTelemetryTest {
    private val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
    private val sut = ReqnrollServerLifecycleTelemetry(send = { name, props -> sent += name to props })

    private fun move(vararg states: LspServerState) = states.forEach(sut::onStateChanged)

    private class DeferredExitDetails {
        private var pending: ((Map<String, Any?>) -> Unit)? = null
        val exitDetails: (onReady: (Map<String, Any?>) -> Unit) -> Unit = { pending = it }
        fun deliver(details: Map<String, Any?>) = pending!!(details)
    }

    private class Harness(exitDetails: (onReady: (Map<String, Any?>) -> Unit) -> Unit) {
        val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
        private val telemetry = ReqnrollServerLifecycleTelemetry({ name, props -> sent += name to props }, exitDetails = exitDetails)
        fun move(vararg states: LspServerState) = states.forEach(telemetry::onStateChanged)
    }

    private fun telemetryWithExitDetails(details: Map<String, Any?>) = Harness { onReady -> onReady(details) }

    private fun telemetryWithExitDetails(delivery: DeferredExitDetails) = Harness(delivery.exitDetails)

    @Test
    fun `a healthy start and normal shutdown send nothing`() {
        move(LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownNormally)

        assertTrue(sent.isEmpty())
    }

    @Test
    fun `shutting down unexpectedly before running is a start failure on attempt 1`() {
        move(LspServerState.Initializing, LspServerState.ShutdownUnexpectedly)

        assertEquals(
            listOf("Server.Process.StartFailed" to mapOf<String, Any?>("Reason" to "StartFailed", "AttemptNumber" to 1)),
            sent,
        )
    }

    @Test
    fun `an unexpected shutdown after running with no sign the process ended is a lost connection, and the restart carries its reason`() {
        move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly,
            LspServerState.Initializing, LspServerState.Running,
        )

        assertEquals(
            listOf(
                "Server.Process.ConnectionLost" to mapOf<String, Any?>("Reason" to "ConnectionLost", "AttemptNumber" to 1),
                "Server.Process.Restarted" to mapOf<String, Any?>("Reason" to "ConnectionLost", "AttemptNumber" to 2),
            ),
            sent,
        )
    }

    @Test
    fun `an unexpected shutdown after running with an exit code is an unexpected exit, and the restart carries its reason`() {
        val withDetails = telemetryWithExitDetails(mapOf("ServerSessionId" to "id-1", "ExitCode" to 134))

        withDetails.move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly,
            LspServerState.Initializing, LspServerState.Running,
        )

        assertEquals(
            listOf(
                "Server.Process.ExitedUnexpectedly" to mapOf<String, Any?>(
                    "ServerSessionId" to "id-1", "ExitCode" to 134, "Reason" to "ProcessExited", "AttemptNumber" to 1,
                ),
                "Server.Process.Restarted" to mapOf<String, Any?>("Reason" to "ProcessExited", "AttemptNumber" to 2),
            ),
            withDetails.sent,
        )
    }

    @Test
    fun `a crash report without an exit code is still an unexpected exit`() {
        val withDetails = telemetryWithExitDetails(mapOf("ServerSessionId" to "id-1", "CrashKind" to "UnhandledException"))

        withDetails.move(LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly)

        assertEquals(listOf("Server.Process.ExitedUnexpectedly"), withDetails.sent.map { it.first })
        assertEquals("ProcessExited", withDetails.sent.single().second["Reason"])
    }

    @Test
    fun `a lost connection carries the exit details that are known but no exit code`() {
        val withDetails = telemetryWithExitDetails(mapOf("ServerSessionId" to "id-1", "UptimeMs" to 114000L))

        withDetails.move(LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly)

        assertEquals(
            listOf(
                "Server.Process.ConnectionLost" to mapOf<String, Any?>(
                    "ServerSessionId" to "id-1", "UptimeMs" to 114000L, "Reason" to "ConnectionLost", "AttemptNumber" to 1,
                ),
            ),
            withDetails.sent,
        )
    }

    @Test
    fun `a restart that comes before the exit details is labelled a lost connection, and a later exit corrects only the same attempt`() {
        val delivery = DeferredExitDetails()
        val withDetails = telemetryWithExitDetails(delivery)

        withDetails.move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly,
            LspServerState.Initializing,
        )
        assertEquals("ConnectionLost", withDetails.sent.single().second["Reason"], "the quick restart sees the stop")

        delivery.deliver(mapOf("ExitCode" to 1))
        withDetails.move(LspServerState.Running, LspServerState.ShutdownUnexpectedly, LspServerState.Initializing)

        assertEquals(
            listOf("Server.Process.Restarted", "Server.Process.ExitedUnexpectedly", "Server.Process.Restarted"),
            withDetails.sent.map { it.first },
        )
        assertEquals(
            "ConnectionLost",
            withDetails.sent.last().second["Reason"],
            "the late exit of attempt 1 must not rewrite the reason for attempt 2's stop",
        )
    }

    @Test
    fun `an exit whose details arrive before any restart makes the restart carry ProcessExited`() {
        val delivery = DeferredExitDetails()
        val withDetails = telemetryWithExitDetails(delivery)

        withDetails.move(LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly)
        delivery.deliver(mapOf("ExitCode" to 134))
        withDetails.move(LspServerState.Initializing)

        assertEquals("ProcessExited", withDetails.sent.last().second["Reason"])
    }

    @Test
    fun `a restart after a start failure reports StartFailed and a higher attempt number`() {
        move(
            LspServerState.Initializing, LspServerState.ShutdownUnexpectedly,
            LspServerState.Initializing, LspServerState.ShutdownUnexpectedly,
        )

        assertEquals(
            listOf("Server.Process.StartFailed", "Server.Process.Restarted", "Server.Process.StartFailed"),
            sent.map { it.first },
        )
        assertEquals(listOf<Any?>(1, 2, 2), sent.map { it.second["AttemptNumber"] })
        assertEquals("StartFailed", sent[1].second["Reason"])
    }

    @Test
    fun `starting again after a normal shutdown is reported with the SessionEnded reason`() {
        move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownNormally,
            LspServerState.Initializing,
        )

        assertEquals(
            listOf("Server.Process.Restarted" to mapOf<String, Any?>("Reason" to "SessionEnded", "AttemptNumber" to 2)),
            sent,
        )
    }

    @Test
    fun `a repeated unexpected shutdown within one attempt is reported once`() {
        move(LspServerState.Initializing, LspServerState.ShutdownUnexpectedly, LspServerState.ShutdownUnexpectedly)

        assertEquals(1, sent.size)
    }

    @Test
    fun `event properties are only the closed Reason and AttemptNumber`() {
        move(LspServerState.Initializing, LspServerState.ShutdownUnexpectedly)

        assertEquals(setOf("Reason", "AttemptNumber"), sent.single().second.keys)
    }

    @Test
    fun `a start failure carries the start details`() {
        val withDetails = mutableListOf<Pair<String, Map<String, Any?>>>()
        val tracked = ReqnrollServerLifecycleTelemetry(
            { name, props -> withDetails += name to props },
            startDetails = { mapOf("ServerSessionId" to "id-1") },
        )

        tracked.onStateChanged(LspServerState.Initializing)
        tracked.onStateChanged(LspServerState.ShutdownUnexpectedly)

        assertEquals(
            mapOf<String, Any?>("ServerSessionId" to "id-1", "Reason" to "StartFailed", "AttemptNumber" to 1),
            withDetails.single().second,
        )
    }

    @Test
    fun `an unexpected exit is sent when its details are ready, and they never replace Reason or AttemptNumber`() {
        val withDetails = mutableListOf<Pair<String, Map<String, Any?>>>()
        var deliver: ((Map<String, Any?>) -> Unit)? = null
        val tracked = ReqnrollServerLifecycleTelemetry(
            { name, props -> withDetails += name to props },
            exitDetails = { deliver = it },
        )

        tracked.onStateChanged(LspServerState.Initializing)
        tracked.onStateChanged(LspServerState.Running)
        tracked.onStateChanged(LspServerState.ShutdownUnexpectedly)
        assertTrue(withDetails.isEmpty(), "waits for the details")

        deliver!!(mapOf("ServerSessionId" to "id-1", "ExitCode" to 134, "Reason" to "ignored"))

        assertEquals(
            listOf(
                "Server.Process.ExitedUnexpectedly" to mapOf<String, Any?>(
                    "ServerSessionId" to "id-1", "ExitCode" to 134, "Reason" to "ProcessExited", "AttemptNumber" to 1,
                ),
            ),
            withDetails,
        )
    }
}
