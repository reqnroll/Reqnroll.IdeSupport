package com.reqnroll.ide.rider.lsp

import com.intellij.platform.lsp.api.LspServerState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ReqnrollServerLifecycleTelemetryTest {
    private val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
    private val sut = ReqnrollServerLifecycleTelemetry { name, props -> sent += name to props }

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
            listOf("ServerStartFailed" to mapOf<String, Any?>("Reason" to "StartFailed", "AttemptNumber" to 1)),
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
                "ServerExitedUnexpectedly" to mapOf<String, Any?>("Reason" to "ProcessExited", "AttemptNumber" to 1),
                "ServerRestarted" to mapOf<String, Any?>("Reason" to "ProcessExited", "AttemptNumber" to 2),
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
            listOf("ServerStartFailed", "ServerRestarted", "ServerStartFailed"),
            sent.map { it.first },
        )
        assertEquals(listOf<Any?>(1, 2, 2), sent.map { it.second["AttemptNumber"] })
        assertEquals("StartFailed", sent[1].second["Reason"])
    }

    @Test
    fun `starting again after a normal shutdown is a user restart`() {
        move(
            LspServerState.Initializing, LspServerState.Running, LspServerState.ShutdownNormally,
            LspServerState.Initializing,
        )

        assertEquals(
            listOf("ServerRestarted" to mapOf<String, Any?>("Reason" to "UserRestart", "AttemptNumber" to 2)),
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
}
