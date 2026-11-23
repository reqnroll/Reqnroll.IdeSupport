package net.reqnroll.idesupport.rider.lsp

import java.util.concurrent.CopyOnWriteArrayList

/**
 * The callbacks [ReqnrollServerEvents] runs after the server reaches Running, and how they are run.
 *
 * [notifyRunning] returns as soon as the work is handed to [dispatch]: the platform calls the
 * server listener on the thread that reads the server's messages, and only releases the connect
 * timeout's latch once the listener returns, so a slow subscriber would stall the server's message
 * processing and, past the timeout, fail the start. The subscribers then run one after another, in
 * registration order, on whatever thread [dispatch] chooses. A seam so the dispatch is testable
 * without a platform.
 *
 * @param dispatch runs a unit of work off the calling thread.
 * @param isCancelled true once the work should be skipped (the project was disposed while it was queued).
 * @param log receives one diagnostic line per dispatch, saying how long the subscribers took.
 * @param logFailure receives a subscriber's failure; it never reaches the caller or the other subscribers.
 * @param nanoTime the clock behind the duration in the log line.
 */
internal class RunningSubscribers(
    private val dispatch: (Runnable) -> Unit,
    private val isCancelled: () -> Boolean,
    private val log: (String) -> Unit,
    private val logFailure: (String, Throwable) -> Unit,
    private val nanoTime: () -> Long = System::nanoTime,
) {
    private val subscribers = CopyOnWriteArrayList<() -> Unit>()

    /** Calls [onRunning] after every transition to running until the returned handle is invoked. */
    fun subscribe(onRunning: () -> Unit): () -> Unit {
        // Wrapped so removal is by this registration, not by lambda equality.
        val registration: () -> Unit = { onRunning() }
        subscribers.add(registration)
        return { subscribers.remove(registration) }
    }

    /** Hands the current subscribers to [dispatch]; does not wait for them. */
    fun notifyRunning() {
        // Taken now, so a subscriber that registers after this transition is not run for it
        // (the readiness gate re-checks the state itself).
        val snapshot = subscribers.toList()
        if (snapshot.isEmpty()) return
        dispatch(Runnable { runAll(snapshot) })
    }

    private fun runAll(snapshot: List<() -> Unit>) {
        if (isCancelled()) return
        val started = nanoTime()
        var failures = 0
        // One failing subscriber must not starve the others.
        snapshot.forEach {
            try {
                it()
            } catch (e: Exception) {
                failures++
                logFailure("ReqnrollServerEvents: a server-running subscriber failed", e)
            }
        }
        val elapsedMs = (nanoTime() - started) / NANOS_PER_MILLI
        log("ReqnrollServerEvents: ran ${snapshot.size} server-running subscriber(s) in $elapsedMs ms ($failures failed)")
    }

    private companion object {
        const val NANOS_PER_MILLI = 1_000_000L
    }
}
