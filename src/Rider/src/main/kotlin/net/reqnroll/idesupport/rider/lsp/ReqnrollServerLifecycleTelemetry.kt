package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.Disposable
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.util.Disposer
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerManagerListener
import com.intellij.platform.lsp.api.LspServerState
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter

/**
 * Turns the Reqnroll language server's [LspServerState] transitions into the client-originated
 * server-lifecycle telemetry events (issue #845), because a dead server cannot report itself:
 *
 * - `ServerStartFailed`: [LspServerState.ShutdownUnexpectedly] before the server ever reached [LspServerState.Running].
 * - `ServerExitedUnexpectedly`: [LspServerState.ShutdownUnexpectedly] after it had been running.
 * - `ServerRestarted`: a new [LspServerState.Initializing] after a failure or exit (`Reason` says which),
 *   or after a normal shutdown (`SessionEnded`; the platform does not say whether the user or the IDE initiated it).
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

    /**
     * Seeds the tracker with a server that already exists when it starts listening (the listener is
     * registered from a post-startup activity, which can run after `fileOpened` started the server).
     * Counts it as attempt 1 without sending anything: how it got into [state] was not observed.
     */
    @Synchronized
    fun seed(state: LspServerState) {
        if (attempt > 0) return
        attempt = 1
        reachedRunning = state == LspServerState.Running
        failureReported = state == LspServerState.ShutdownUnexpectedly || state == LspServerState.ShutdownNormally
    }

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
                        properties(lastFailureReason ?: RiderTelemetryTransmitter.SERVER_FAILURE_REASON_SESSION_ENDED),
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
        val manager = LspServerManager.getInstance(project)
        // Seed from a server that is already up (its Initializing transition happened before we listened),
        // otherwise the first real Initializing would be missed and AttemptNumber would be off by one.
        manager.getServersForProvider(ReqnrollLspServerSupportProvider::class.java).firstOrNull()
            ?.let { tracker.seed(it.state) }
        manager.addLspServerManagerListener(
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
