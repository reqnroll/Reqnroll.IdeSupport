package com.reqnroll.ide.rider.testrunner

import java.util.concurrent.TimeUnit

/** How [DotnetTestProcessWaiter.await] ended. */
enum class ProcessWaitResult { COMPLETED, TIMED_OUT, CANCELLED }

/**
 * Waits for the `dotnet test` child process (issue #981). Replaces one blocking `waitFor(timeout)`
 * so that the user's cancel button is honoured within [pollMillis], and so that giving up on the
 * process (timeout or cancel) ends the whole process tree — `dotnet test` spawns MSBuild node and
 * testhost children that `destroyForcibly()` on the parent alone leaves running in the background.
 */
internal object DotnetTestProcessWaiter {
    /** Default budget, build included. Override with the [TIMEOUT_PROPERTY] system property. */
    const val DEFAULT_TIMEOUT_SECONDS = 120L
    const val TIMEOUT_PROPERTY = "reqnroll.rider.dotnetTestTimeoutSeconds"
    private const val DEFAULT_POLL_MILLIS = 100L
    private const val KILL_GRACE_SECONDS = 5L

    /** [raw] if it is a positive integer, otherwise [DEFAULT_TIMEOUT_SECONDS]. */
    fun resolveTimeoutSeconds(raw: String? = System.getProperty(TIMEOUT_PROPERTY)): Long =
        raw?.trim()?.toLongOrNull()?.takeIf { it > 0 } ?: DEFAULT_TIMEOUT_SECONDS

    /**
     * Blocks until [process] exits ([ProcessWaitResult.COMPLETED]), [timeoutMillis] elapses
     * ([ProcessWaitResult.TIMED_OUT]) or [isCanceled] returns true ([ProcessWaitResult.CANCELLED]).
     * On the latter two the process and all its descendants are killed before returning.
     */
    fun await(
        process: Process,
        timeoutMillis: Long,
        isCanceled: () -> Boolean,
        pollMillis: Long = DEFAULT_POLL_MILLIS,
    ): ProcessWaitResult {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
        while (true) {
            if (isCanceled()) {
                killTree(process)
                return ProcessWaitResult.CANCELLED
            }
            val remainingMillis = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime())
            if (remainingMillis <= 0) {
                killTree(process)
                return ProcessWaitResult.TIMED_OUT
            }
            if (process.waitFor(minOf(pollMillis, remainingMillis), TimeUnit.MILLISECONDS)) {
                return ProcessWaitResult.COMPLETED
            }
        }
    }

    /** Kills [process]'s descendants (snapshotted first, before the parent's death orphans them) and then the process itself. */
    fun killTree(process: Process) {
        val handle = process.toHandle()
        val descendants = handle.descendants().toList()
        descendants.forEach { it.destroyForcibly() }
        process.destroyForcibly()
        // destroyForcibly is asynchronous; give the kill a moment to land so the caller sees a dead process.
        process.waitFor(KILL_GRACE_SECONDS, TimeUnit.SECONDS)
    }
}
