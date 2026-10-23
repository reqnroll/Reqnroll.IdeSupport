package net.reqnroll.idesupport.rider.lsp

import com.intellij.platform.lsp.api.LspServerState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ReqnrollServerLifecycleTelemetryTest {
    private val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
    private val sut = ReqnrollServerLifecycleTelemetry(send = { name, props -> sent += name to props })

    private fun move(vararg states: LspServerState) = states.forEach(sut::onStateChanged)

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
    fun `an unexpected shutdown after running is an unexpected exit, and the restart carries its reason`() {
        move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownUnexpectedly,
            LspServerState.Initializing, LspServerState.Running,
        )

        assertEquals(
            listOf(
                "Server.Process.ExitedUnexpectedly" to mapOf<String, Any?>("Reason" to "ProcessExited", "AttemptNumber" to 1),
                "Server.Process.Restarted" to mapOf<String, Any?>("Reason" to "ProcessExited", "AttemptNumber" to 2),
            ),
            sent,
        )
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
