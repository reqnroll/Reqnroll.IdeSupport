package net.reqnroll.idesupport.rider.lsp

import com.intellij.platform.lsp.api.LspServerState
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter

/**
 * Turns the Reqnroll language server's [LspServerState] transitions into the client-originated
 * server-lifecycle telemetry events, because a dead server cannot report itself:
 *
 * - `Server.Process.StartFailed`: [LspServerState.ShutdownUnexpectedly] before the server ever reached [LspServerState.Running].
 * - `Server.Process.ExitedUnexpectedly`: [LspServerState.ShutdownUnexpectedly] after it had been running.
 * - `Server.Process.Restarted`: a new [LspServerState.Initializing] after a failure or exit (`Reason` says which),
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

    @Synchronized
    fun onStateChanged(state: LspServerState) {
        when (state) {
            LspServerState.Initializing -> {
                attempt += 1
                reachedRunning = false
                failureReported = false
                if (attempt > 1) {
                    send(
                        RiderTelemetryTransmitter.SERVER_PROCESS_RESTARTED,
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
                    send(RiderTelemetryTransmitter.SERVER_PROCESS_EXITED_UNEXPECTEDLY, properties(reason))
                } else {
                    val reason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_START_FAILED
                    lastFailureReason = reason
                    send(RiderTelemetryTransmitter.SERVER_PROCESS_START_FAILED, properties(reason))
                }
                reachedRunning = false
            }
            else -> Unit // ShutdownNormally: a requested stop, not a failure.
        }
    }

    private fun properties(reason: String): Map<String, Any?> =
        mapOf("Reason" to reason, "AttemptNumber" to maxOf(attempt, 1))
}
