package com.reqnroll.ide.rider.testrunner

import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Issue #982: the timeout must fire even when the child produces no output and never exits. */
class BoundedProcessRunnerTest {
    private val sh = File("/bin/sh")

    @Test
    fun `a silent child that never exits is abandoned at the timeout and killed`() {
        assumeTrue(sh.isFile)
        // exec replaces the shell so the child IS the sleeping process (no extra descendant).
        val marker = "reqnroll982-" + System.nanoTime()
        val started = System.nanoTime()
        val result = BoundedProcessRunner.run(listOf("/bin/sh", "-c", "exec sleep 300 # $marker"), 1_000)
        val elapsedMs = (System.nanoTime() - started) / 1_000_000

        assertNull(result)
        assertTrue(elapsedMs < 10_000, "run() took ${elapsedMs}ms; the timeout did not fire")
    }

    @Test
    fun `a hung child and the descendant it spawned are both killed`() {
        assumeTrue(sh.isFile)
        val pidFile = File.createTempFile("reqnroll982", ".pid")
        try {
            val result = BoundedProcessRunner.run(
                listOf("/bin/sh", "-c", "sleep 300 & echo \$! > '${pidFile.absolutePath}'; wait"), 1_500)
            assertNull(result)

            val pid = pidFile.readText().trim().toLong()
            Thread.sleep(500)
            val handle = ProcessHandle.of(pid)
            assertTrue(!handle.isPresent || !handle.get().isAlive, "grandchild $pid survived the timeout")
        } finally {
            pidFile.delete()
        }
    }

    @Test
    fun `a child that exits returns its exit code and stdout`() {
        assumeTrue(sh.isFile)
        val result = assertNotNull(BoundedProcessRunner.run(listOf("/bin/sh", "-c", "echo hello; exit 3"), 10_000))
        assertEquals(3, result.exitCode)
        assertEquals("hello", result.output.trim())
    }

    @Test
    fun `a descendant holding stdout open after the child exits does not block the read`() {
        assumeTrue(sh.isFile)
        val started = System.nanoTime()
        val result = assertNotNull(BoundedProcessRunner.run(listOf("/bin/sh", "-c", "echo done; sleep 300 &"), 20_000))
        val elapsedMs = (System.nanoTime() - started) / 1_000_000

        assertEquals(0, result.exitCode)
        assertEquals("done", result.output.trim())
        assertTrue(elapsedMs < 15_000, "run() took ${elapsedMs}ms waiting on the inherited pipe")
    }
}
