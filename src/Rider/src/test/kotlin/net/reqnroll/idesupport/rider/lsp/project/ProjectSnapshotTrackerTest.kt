package net.reqnroll.idesupport.rider.lsp.project

import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ProjectSnapshotTrackerTest {
    @Test
    fun `an empty first snapshot is not processed`() {
        assertFalse(ProjectSnapshotTracker().shouldProcess(emptyMap()))
    }

    @Test
    fun `a first non-empty snapshot is processed`() {
        assertTrue(ProjectSnapshotTracker().shouldProcess(mapOf("a.csproj" to "net9.0")))
    }

    @Test
    fun `an identical snapshot is skipped`() {
        val tracker = ProjectSnapshotTracker()
        assertTrue(tracker.shouldProcess(mapOf("a.csproj" to "net9.0")))
        assertFalse(tracker.shouldProcess(mapOf("a.csproj" to "net9.0")))
    }

    @Test
    fun `a changed TFM for the same project is processed again`() {
        val tracker = ProjectSnapshotTracker()
        tracker.shouldProcess(mapOf("a.csproj" to ""))
        assertTrue(tracker.shouldProcess(mapOf("a.csproj" to "net9.0")))
    }

    @Test
    fun `concurrent identical snapshots are processed exactly once`() {
        repeat(200) {
            val tracker = ProjectSnapshotTracker()
            val threads = 8
            val start = CountDownLatch(1)
            val trues = AtomicInteger()
            val pool = Executors.newFixedThreadPool(threads)
            try {
                val futures = (1..threads).map {
                    pool.submit {
                        start.await()
                        if (tracker.shouldProcess(mapOf("a.csproj" to "net9.0"))) trues.incrementAndGet()
                    }
                }
                start.countDown()
                futures.forEach { it.get(10, TimeUnit.SECONDS) }
            } finally {
                pool.shutdownNow()
            }
            assertEquals(1, trues.get())
        }
    }
}
