package net.reqnroll.idesupport.rider.lsp

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerRestartPolicyTest {
    private var now = 0L

    private fun policy() = ServerRestartPolicy(maxRestarts = 4, windowMs = 180_000, clock = { now })

    @Test
    fun allowsRestartsUpToTheCap() {
        val policy = policy()
        repeat(4) { assertTrue(policy.tryAcquire()) }
    }

    @Test
    fun refusesOnceTheCapIsReachedWithinTheWindow() {
        val policy = policy()
        repeat(4) { policy.tryAcquire() }
        now = 179_999
        assertFalse(policy.tryAcquire())
    }

    @Test
    fun allowsAgainOnceOldRestartsLeaveTheWindow() {
        val policy = policy()
        repeat(4) { policy.tryAcquire() }
        now = 180_000
        assertTrue(policy.tryAcquire())
    }

    @Test
    fun refusedAttemptsAreNotRecorded() {
        val policy = policy()
        repeat(4) { policy.tryAcquire() }
        now = 100_000
        assertFalse(policy.tryAcquire())
        now = 180_000
        assertTrue(policy.tryAcquire())
    }
}
