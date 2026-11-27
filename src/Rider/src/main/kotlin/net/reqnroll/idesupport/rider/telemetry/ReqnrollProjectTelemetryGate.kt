package net.reqnroll.idesupport.rider.telemetry

/**
 * Holds one solution's telemetry until its language server reports that the solution contains a
 * project that uses Reqnroll (`reqnroll/reqnrollProjectDetected`), so a solution without Reqnroll
 * sends nothing but errors, server start failures, install and upgrade. The counterpart of
 * `ReqnrollProjectTelemetryGate.cs` in VS and `reqnrollProjectGate.ts` in VS Code.
 *
 * Unlike those, there is one gate per project (held by `ReqnrollServerEvents`), because one Rider
 * process can have several solutions open, each with its own server; project-scoped events go
 * through `RiderTelemetryTransmitter.transmit(project, ...)`. The server sends the notification
 * before anything it reports about the Reqnroll project, so the gate is open by the time those
 * arrive. Held events are sent in order on [open]; past [MAX_HELD_EVENTS] they are dropped, and so
 * is whatever is still held when the session ends or the solution closes. [reset] closes the gate
 * again whenever a new server process starts.
 */
class ReqnrollProjectTelemetryGate {
    private val lock = Any()
    private var held: MutableList<() -> Unit>? = mutableListOf()
    private val openedListeners = mutableListOf<() -> Unit>()

    val isOpen: Boolean
        get() = synchronized(lock) { held == null }

    fun send(eventName: String, send: () -> Unit) {
        if (!isAlwaysAllowed(eventName)) {
            synchronized(lock) {
                val queue = held
                if (queue != null) {
                    if (queue.size < MAX_HELD_EVENTS) queue.add(send)
                    return
                }
            }
        }
        send()
    }

    /** Runs [listener] once when the gate opens, or right away if it is already open. */
    fun onOpened(listener: () -> Unit) {
        val runNow = synchronized(lock) {
            if (held == null) true else { openedListeners.add(listener); false }
        }
        if (runNow) runSafely(listener)
    }

    /**
     * Closes the gate for a new server session, dropping whatever the previous session still held.
     * Listeners waiting for the gate to open keep waiting.
     */
    fun reset() {
        synchronized(lock) {
            val queue = held
            if (queue == null) held = mutableListOf() else queue.clear()
        }
    }

    fun open() {
        val (queue, listeners) = synchronized(lock) {
            val queue = held ?: return
            held = null
            val listeners = openedListeners.toList()
            openedListeners.clear()
            queue to listeners
        }
        queue.forEach(::runSafely)
        listeners.forEach(::runSafely)
    }

    private fun runSafely(action: () -> Unit) {
        try {
            action()
        } catch (ex: Exception) {
            // Telemetry must never break the plugin, and one failure must not stop the rest.
        }
    }

    companion object {
        /** Upper bound on events held while no Reqnroll project has been reported. */
        const val MAX_HELD_EVENTS = 200

        /**
         * Errors, server start failures, and install/upgrade: sent even before a Reqnroll project is
         * seen, because they are worth knowing about whatever the solution contains. The opt-out
         * still applies.
         */
        fun isAlwaysAllowed(eventName: String): Boolean =
            eventName.startsWith("Error.") ||
                eventName == RiderTelemetryTransmitter.LIFECYCLE_EXTENSION_INSTALLED ||
                eventName == RiderTelemetryTransmitter.LIFECYCLE_EXTENSION_UPGRADED ||
                eventName == RiderTelemetryTransmitter.SERVER_PROCESS_START_FAILED ||
                eventName == RiderTelemetryTransmitter.SERVER_PROCESS_EXITED_UNEXPECTEDLY ||
                eventName == RiderTelemetryTransmitter.SERVER_PROCESS_CONNECTION_LOST
    }
}
