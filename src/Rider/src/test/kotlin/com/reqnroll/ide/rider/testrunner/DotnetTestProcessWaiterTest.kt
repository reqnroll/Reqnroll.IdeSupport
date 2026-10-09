package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.util.SystemInfo
import java.util.concurrent.TimeUnit
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class DotnetTestProcessWaiterTest {
    private val started = mutableListOf<Process>()

    @AfterTest
    fun cleanup() {
        started.forEach { p ->
            p.toHandle().descendants().forEach { it.destroyForcibly() }
            p.destroyForcibly()
        }
    }

    /** A parent shell with one long-lived child, mimicking `dotnet test` + testhost/MSBuild; null where no POSIX shell exists. */
    private fun startParentWithChild(sleepSeconds: Int = 300): Process? {
        if (SystemInfo.isWindows) return null
        return ProcessBuilder("sh", "-c", "sleep $sleepSeconds & wait")
            .start().also { started += it }
    }

    private fun awaitDescendants(process: Process): List<ProcessHandle> {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
        while (System.nanoTime() < deadline) {
            val d = process.toHandle().descendants().toList()
            if (d.isNotEmpty()) return d
            Thread.sleep(20)
        }
        error("child process never appeared")
    }

    private fun awaitDead(handle: ProcessHandle): Boolean {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (System.nanoTime() < deadline) {
            if (!handle.isAlive) return true
            Thread.sleep(20)
        }
        return !handle.isAlive
    }

    @Test
    fun `completes normally when the process exits within the budget`() {
        val process = (if (SystemInfo.isWindows) ProcessBuilder("cmd", "/c", "exit 0") else ProcessBuilder("sh", "-c", "exit 0"))
            .start().also { started += it }

        val result = DotnetTestProcessWaiter.await(process, 10_000, { false })

        assertEquals(ProcessWaitResult.COMPLETED, result)
        assertEquals(0, process.exitValue())
    }

    @Test
    fun `times out and kills the descendants as well as the process`() {
        val process = startParentWithChild() ?: return
        val children = awaitDescendants(process)

        val result = DotnetTestProcessWaiter.await(process, 300, { false }, pollMillis = 20)

        assertEquals(ProcessWaitResult.TIMED_OUT, result)
        assertFalse(process.isAlive)
        assertTrue(children.all { awaitDead(it) }, "descendant of the timed-out process was left running")
    }

    @Test
    fun `cancellation stops promptly and kills the descendants`() {
        val process = startParentWithChild() ?: return
        val children = awaitDescendants(process)
        val startNanos = System.nanoTime()

        val result = DotnetTestProcessWaiter.await(process, 60_000, { true }, pollMillis = 20)

        assertEquals(ProcessWaitResult.CANCELLED, result)
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - startNanos) < 5_000, "cancel was not honoured promptly")
        assertFalse(process.isAlive)
        assertTrue(children.all { awaitDead(it) }, "descendant of the cancelled process was left running")
    }

    @Test
    fun `cancellation raised mid-wait is noticed within a poll slice`() {
        val process = startParentWithChild() ?: return
        val polls = java.util.concurrent.atomic.AtomicInteger()

        val result = DotnetTestProcessWaiter.await(process, 60_000, { polls.incrementAndGet() > 3 }, pollMillis = 20)

        assertEquals(ProcessWaitResult.CANCELLED, result)
    }

    @Test
    fun `timeout resolves from a positive integer and otherwise falls back to the default`() {
        assertEquals(600L, DotnetTestProcessWaiter.resolveTimeoutSeconds("600"))
        assertEquals(600L, DotnetTestProcessWaiter.resolveTimeoutSeconds(" 600 "))
        assertEquals(DotnetTestProcessWaiter.DEFAULT_TIMEOUT_SECONDS, DotnetTestProcessWaiter.resolveTimeoutSeconds(null))
        assertEquals(DotnetTestProcessWaiter.DEFAULT_TIMEOUT_SECONDS, DotnetTestProcessWaiter.resolveTimeoutSeconds("abc"))
        assertEquals(DotnetTestProcessWaiter.DEFAULT_TIMEOUT_SECONDS, DotnetTestProcessWaiter.resolveTimeoutSeconds("0"))
        assertEquals(DotnetTestProcessWaiter.DEFAULT_TIMEOUT_SECONDS, DotnetTestProcessWaiter.resolveTimeoutSeconds("-5"))
    }
}
