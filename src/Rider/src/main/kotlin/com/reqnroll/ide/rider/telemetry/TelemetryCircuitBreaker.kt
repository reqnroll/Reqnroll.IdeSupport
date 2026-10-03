package com.reqnroll.ide.rider.telemetry

import java.util.concurrent.atomic.AtomicBoolean

/**
 * Best-effort delivery policy for telemetry (issue #859): after the first definitive failure to
 * reach the analytics endpoint the transmitter stops touching the network for the rest of the
 * session and tells the user exactly once. Same behaviour and wording as VS's
 * `TelemetryCircuitBreaker.cs` and VS Code's `telemetryCircuitBreaker.ts`.
 *
 * [onFirstFailure] receives the one-time notice (written to the Reqnroll console and log file);
 * [onSuppressedFailure] receives every later failure and is for verbose logging only.
 */
class TelemetryCircuitBreaker(
    private val onFirstFailure: (String) -> Unit,
    private val onSuppressedFailure: (String) -> Unit = {},
) {
    private val open = AtomicBoolean(false)

    /** True once a failure has been recorded; callers must then skip all network work. */
    val isOpen: Boolean get() = open.get()

    fun recordFailure(reason: String) {
        if (open.compareAndSet(false, true)) {
            onFirstFailure(UNAVAILABLE_NOTICE)
            onSuppressedFailure("Telemetry failure: $reason")
        } else {
            onSuppressedFailure("Telemetry failure (suppressed): $reason")
        }
    }

    companion object {
        /** The single per-session notation; identical in every IDE. */
        const val UNAVAILABLE_NOTICE = "Telemetry endpoint unreachable; telemetry for this session will be dropped"
    }
}
