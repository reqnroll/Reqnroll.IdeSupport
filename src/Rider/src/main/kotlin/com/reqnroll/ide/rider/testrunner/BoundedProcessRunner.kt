package com.reqnroll.ide.rider.testrunner

import java.util.concurrent.TimeUnit

/** The result of a [BoundedProcessRunner.run] that finished within its timeout. */
internal class BoundedProcessResult(val exitCode: Int, val output: String)

/**
 * Runs a short-lived child process and captures its stdout with the timeout actually enforced
 * (issue #982). Reading stdout to EOF *before* `waitFor(timeout)` makes the timeout dead code: a
 * hung child — or a grandchild that inherited the pipe and outlives the child — keeps the read
 * blocked forever. Here stdout is drained on a daemon thread while the caller waits on the process
 * itself, and a process that overruns is killed together with its descendants.
 */
internal object BoundedProcessRunner {
    /** After the process exits, how long to wait for the stdout drain to hit EOF (a lingering grandchild can hold the pipe open). */
    private const val DRAIN_GRACE_MILLIS = 2_000L

    /**
     * Starts [command] with stderr discarded (an un-drained stderr pipe would block the child once
     * its OS buffer fills) and returns its exit code and stdout, or null if it did not exit within
     * [timeoutMillis] (the process tree is then destroyed). Exceptions from starting the process
     * propagate to the caller.
     */
    fun run(command: List<String>, timeoutMillis: Long): BoundedProcessResult? {
        val process = ProcessBuilder(command)
            .redirectError(ProcessBuilder.Redirect.DISCARD)
            .start()

        val captured = StringBuilder()
        val drain = Thread({
            try {
                process.inputStream.bufferedReader().use { reader ->
                    val buffer = CharArray(8192)
                    while (true) {
                        val read = reader.read(buffer)
                        if (read < 0) break
                        synchronized(captured) { captured.append(buffer, 0, read) }
                    }
                }
            } catch (_: java.io.IOException) {
                // Stream closed by destroy or by the grace-period cleanup below — whatever was captured stands.
            }
        }, "reqnroll-process-output-drain").apply { isDaemon = true }
        drain.start()

        if (!process.waitFor(timeoutMillis, TimeUnit.MILLISECONDS)) {
            destroyTree(process)
            return null
        }

        drain.join(DRAIN_GRACE_MILLIS)
        if (drain.isAlive) {
            // A descendant still holds the pipe; the child's own output is complete, so stop waiting.
            runCatching { process.inputStream.close() }
        }
        val output = synchronized(captured) { captured.toString() }
        return BoundedProcessResult(process.exitValue(), output)
    }

    private fun destroyTree(process: Process) {
        // Descendants first: once the parent dies they are re-parented and can no longer be enumerated from it.
        runCatching { process.toHandle().descendants().forEach { it.destroyForcibly() } }
        process.destroyForcibly()
    }
}
