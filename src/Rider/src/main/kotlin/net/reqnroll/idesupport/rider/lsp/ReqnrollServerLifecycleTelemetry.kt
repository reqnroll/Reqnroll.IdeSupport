package net.reqnroll.idesupport.rider.lsp

import com.intellij.platform.lsp.api.LspServerState
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter

/**
 * Turns the Reqnroll language server's [LspServerState] transitions into the client-originated
 * server-lifecycle telemetry events, because a dead server cannot report itself:
 *
 * - `Server.Process.StartFailed`: [LspServerState.ShutdownUnexpectedly] before the server ever reached [LspServerState.Running].
 * - `Server.Process.ExitedUnexpectedly`: [LspServerState.ShutdownUnexpectedly] after it had been running, when the
 *   process is known to have ended (the details carry an `ExitCode` or a parsed crash report).
 * - `Server.Process.ConnectionLost`: the same state change when the process has not ended once its exit details have
 *   been awaited. The platform reports the stop without checking the process, so this is a lost or stalled
 *   connection to a server that may still be alive, not a crash.
 * - `Server.Process.Restarted`: a new [LspServerState.Initializing] after a failure or exit (`Reason` says which),
 *   or after a normal shutdown (`SessionEnded`; the platform does not say whether the user or the IDE initiated it).
 *
 * `AttemptNumber` is the 1-based start attempt in this IDE session. Each attempt reports at most one
 * failure. Pure (the sink and the detail sources are injected) so it is unit-testable without a platform fixture.
 *
 * [startDetails] adds `ServerSessionId` to a start failure. [exitDetails] supplies an unexpected exit's details
 * (`ServerSessionId`, `ExitCode`, `UptimeMs`, crash report) to the callback it is given; it may do so later and
 * on another thread, since they are only complete once the process has finished terminating.
 *
 * Whether the process ended is only known when the details arrive, but a quick restart can come first, so the
 * stop is first recorded as a lost connection (the next `Restarted` reads it) and corrected to a process exit
 * if the details show the process ended and no newer attempt has started.
 */
class ReqnrollServerLifecycleTelemetry(
    private val send: (eventName: String, properties: Map<String, Any?>) -> Unit,
    private val startDetails: () -> Map<String, Any?> = { emptyMap() },
    private val exitDetails: (onReady: (Map<String, Any?>) -> Unit) -> Unit = { it(emptyMap()) },
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
                    lastFailureReason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_CONNECTION_LOST
                    val stoppedAttempt = attempt
                    exitDetails { details -> reportStopAfterRunning(stoppedAttempt, details) }
                } else {
                    val reason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_START_FAILED
                    lastFailureReason = reason
                    send(RiderTelemetryTransmitter.SERVER_PROCESS_START_FAILED, startDetails() + properties(reason))
                }
                reachedRunning = false
            }
            else -> Unit // ShutdownNormally: a requested stop, not a failure.
        }
    }

    private fun reportStopAfterRunning(stoppedAttempt: Int, details: Map<String, Any?>) {
        if (processEnded(details)) {
            synchronized(this) {
                if (attempt == stoppedAttempt && lastFailureReason == RiderTelemetryTransmitter.SERVER_FAILURE_REASON_CONNECTION_LOST) {
                    lastFailureReason = RiderTelemetryTransmitter.SERVER_FAILURE_REASON_PROCESS_EXITED
                }
            }
            send(
                RiderTelemetryTransmitter.SERVER_PROCESS_EXITED_UNEXPECTEDLY,
                details + properties(RiderTelemetryTransmitter.SERVER_FAILURE_REASON_PROCESS_EXITED, stoppedAttempt),
            )
        } else {
            send(
                RiderTelemetryTransmitter.SERVER_PROCESS_CONNECTION_LOST,
                details + properties(RiderTelemetryTransmitter.SERVER_FAILURE_REASON_CONNECTION_LOST, stoppedAttempt),
            )
        }
    }

    /** An exit code or a crash report is evidence the process ended; without either it may still be running. */
    private fun processEnded(details: Map<String, Any?>): Boolean =
        details.containsKey(ReqnrollServerProcessTracker.EXIT_CODE_PROPERTY) ||
            details.containsKey(ServerCrashReport.CRASH_KIND)

    private fun properties(reason: String, attemptNumber: Int = attempt): Map<String, Any?> =
        mapOf("Reason" to reason, "AttemptNumber" to maxOf(attemptNumber, 1))
}
