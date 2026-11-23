package net.reqnroll.idesupport.rider.lsp

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RunningSubscribersTest {
    private val queued = ArrayList<Runnable>()
    private val logs = ArrayList<String>()
    private val failures = ArrayList<Pair<String, Throwable>>()
    private var cancelled = false
    private var now = 0L

    private fun subscribers() = RunningSubscribers(
        dispatch = { queued.add(it) },
        isCancelled = { cancelled },
        log = { logs.add(it) },
        logFailure = { message, error -> failures.add(message to error) },
        nanoTime = { now },
    )

    private fun runQueued() = queued.toList().also { queued.clear() }.forEach { it.run() }

    @Test
    fun notifyingRunsNothingOnTheCallingThread() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        subscribers.subscribe { calls.add("a") }

        subscribers.notifyRunning()

        assertTrue(calls.isEmpty())
        assertEquals(1, queued.size)

        runQueued()
        assertEquals(listOf("a"), calls)
    }

    @Test
    fun subscribersRunInRegistrationOrder() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        subscribers.subscribe { calls.add("a") }
        subscribers.subscribe { calls.add("b") }
        subscribers.subscribe { calls.add("c") }

        subscribers.notifyRunning()
        runQueued()

        assertEquals(listOf("a", "b", "c"), calls)
    }

    @Test
    fun aFailingSubscriberDoesNotStarveTheOthersOrEscape() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        subscribers.subscribe { calls.add("a") }
        subscribers.subscribe { throw IllegalStateException("boom") }
        subscribers.subscribe { calls.add("c") }

        subscribers.notifyRunning()
        runQueued()

        assertEquals(listOf("a", "c"), calls)
        assertEquals(1, failures.size)
        assertEquals("boom", failures.single().second.message)
        assertTrue(logs.single().contains("3 server-running subscriber(s)"))
        assertTrue(logs.single().contains("(1 failed)"))
    }

    @Test
    fun workIsSkippedOnceCancelled() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        subscribers.subscribe { calls.add("a") }

        subscribers.notifyRunning()
        cancelled = true
        runQueued()

        assertTrue(calls.isEmpty())
        assertTrue(logs.isEmpty())
    }

    @Test
    fun noSubscribersDispatchesNothing() {
        subscribers().notifyRunning()

        assertTrue(queued.isEmpty())
    }

    @Test
    fun theLogLineReportsHowLongTheSubscribersTook() {
        val subscribers = subscribers()
        subscribers.subscribe { now += 2_500_000L }

        subscribers.notifyRunning()
        runQueued()

        assertEquals(1, logs.size)
        assertTrue(logs.single(), logs.single().contains("in 2 ms"))
    }

    @Test
    fun anUnsubscribedHandleIsNotCalledForLaterTransitions() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        val unsubscribe = subscribers.subscribe { calls.add("a") }
        subscribers.subscribe { calls.add("b") }

        unsubscribe()
        subscribers.notifyRunning()
        runQueued()

        assertEquals(listOf("b"), calls)
    }

    @Test
    fun aSubscriberAddedAfterTheTransitionIsNotRunForIt() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        subscribers.subscribe { calls.add("a") }

        subscribers.notifyRunning()
        subscribers.subscribe { calls.add("late") }
        runQueued()

        assertEquals(listOf("a"), calls)
    }

    @Test
    fun aSubscriberMayUnsubscribeItselfWhileRunning() {
        val subscribers = subscribers()
        val calls = ArrayList<String>()
        lateinit var unsubscribe: () -> Unit
        unsubscribe = subscribers.subscribe { calls.add("once"); unsubscribe() }

        subscribers.notifyRunning()
        runQueued()
        subscribers.notifyRunning()
        runQueued()

        assertEquals(listOf("once"), calls)
    }

    @Test
    fun aFailingDispatchDoesNotEscapeAndIsReported() {
        val subscribers = RunningSubscribers(
            dispatch = { throw IllegalStateException("pool is shutting down") },
            isCancelled = { cancelled },
            log = { logs.add(it) },
            logFailure = { message, error -> failures.add(message to error) },
            nanoTime = { now },
        )
        subscribers.subscribe { }

        subscribers.notifyRunning()

        assertEquals(1, failures.size)
        assertEquals("pool is shutting down", failures.single().second.message)
        assertTrue(logs.isEmpty())
    }
}
