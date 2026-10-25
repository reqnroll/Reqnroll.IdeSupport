package net.reqnroll.idesupport.rider.lsp

/**
 * Bounds automatic server restarts to [maxRestarts] within any [windowMs] window, matching
 * vscode-languageclient's restart cap. Not thread-safe on its own; callers synchronize.
 */
class ServerRestartPolicy(
    private val maxRestarts: Int = DEFAULT_MAX_RESTARTS,
    private val windowMs: Long = DEFAULT_WINDOW_MS,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val restartTimes = ArrayDeque<Long>()

    /** Records a restart and returns true if one is allowed now; returns false (recording nothing) once the cap is hit. */
    @Synchronized
    fun tryAcquire(): Boolean {
        val now = clock()
        while (restartTimes.isNotEmpty() && now - restartTimes.first() >= windowMs) {
            restartTimes.removeFirst()
        }
        if (restartTimes.size >= maxRestarts) return false
        restartTimes.addLast(now)
        return true
    }

    companion object {
        const val DEFAULT_MAX_RESTARTS = 4
        const val DEFAULT_WINDOW_MS = 3 * 60 * 1000L
    }
}
