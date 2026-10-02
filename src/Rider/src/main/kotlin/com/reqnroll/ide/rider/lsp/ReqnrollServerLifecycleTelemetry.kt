package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.Disposable
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.util.Disposer
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerManagerListener
import com.intellij.platform.lsp.api.LspServerState
import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter

/**
 * Turns the Reqnroll language server's [LspServerState] transitions into the client-originated
 * server-lifecycle telemetry events (issue #845), because a dead server cannot report itself:
 *
 * - `ServerStartFailed`: [LspServerState.ShutdownUnexpectedly] before the server ever reached [LspServerState.Running].
 * - `ServerExitedUnexpectedly`: [LspServerState.ShutdownUnexpectedly] after it had been running.
 * - `ServerRestarted`: a new [LspServerState.Initializing] after a failure or exit (`Reason` says which),
 *   or after a normal shutdown (`UserRestart`).
 *
 * `AttemptNumber` is the 1-based start attempt in this IDE session. Each attempt reports at most one
 * failure. Pure (the sink is injected) so it is unit-testable without a platform fixture.
 */
class ReqnrollServerLifecycleTelemetry(
    private val send: (eventName: String, properties: Map<String, Any?>) -> Unit,
) {
    private var attempt = 0
    private var reachedRunning = false
    private var failureReported = false
    private var lastFailureReason: String? = null

    @Synchronized
    fun onStateChanged(state: LspServerState) {
        when (state) {
            LspServerState.Initializing -> {
                attempt += 1
                reachedRunning = false
                failureReported = false
                if (attempt > 1) {
                    send(
                        RiderTelemetryTransmitter.SERVER_RESTARTED,
                        properties(lastFailureReason ?: RiderTelemetryTransmitter.SERVER_FAILURE_REASON_USER_RESTART),
                    )
                    lastFailureReason = null
                }
            }
            LspServerState.Running -> reachedRunning = true
            LspServerState.ShutdownUnexpectedly -> {
                if (failureReported) return
                failureReported = true
                if (reachedRunning) {
                    val reason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_PROCESS_EXITED
                    lastFailureReason = reason
                    send(RiderTelemetryTransmitter.SERVER_EXITED_UNEXPECTEDLY, properties(reason))
                } else {
                    val reason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_START_FAILED
                    lastFailureReason = reason
                    send(RiderTelemetryTransmitter.SERVER_START_FAILED, properties(reason))
                }
                reachedRunning = false
            }
            else -> Unit // ShutdownNormally: a requested stop, not a failure.
        }
    }

    private fun properties(reason: String): Map<String, Any?> =
        mapOf("Reason" to reason, "AttemptNumber" to maxOf(attempt, 1))
}

/**
 * Subscribes one [ReqnrollServerLifecycleTelemetry] per project to [LspServerManager] state changes
 * of the Reqnroll server (registered in `plugin.xml` as a `postStartupActivity`), forwarding the
 * resulting events through [RiderTelemetryTransmitter].
 */
class ReqnrollServerLifecycleListener : ProjectActivity {
    override suspend fun execute(project: Project) {
        val tracker = ReqnrollServerLifecycleTelemetry(RiderTelemetryTransmitter::transmit)
        val lifetime: Disposable = Disposer.newDisposable(project, "ReqnrollServerLifecycleListener")
        LspServerManager.getInstance(project).addLspServerManagerListener(
            object : LspServerManagerListener {
                override fun serverStateChanged(lspServer: LspServer) {
                    if (lspServer.providerClass == ReqnrollLspServerSupportProvider::class.java) {
                        tracker.onStateChanged(lspServer.state)
                    }
                }
            },
            lifetime,
            false,
        )
    }
}
