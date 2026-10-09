package com.reqnroll.ide.rider.lsp.project

import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/** Issue #987: the check-then-register race and N-files-opened-during-startup => N actions. */
class ServerReadinessGateTest {
    /**
     * Stands in for `LspServerManager`: [isRunning] reads the current state, and [reachRunning]
     * flips it and notifies only the listeners subscribed at that moment — like the platform's
     * `EventDispatcher` multicaster, a listener registered after the transition never hears of it.
     */
    private class FakeServer(running: Boolean = false) : ServerStateSource {
        @Volatile
        var running = running
        val listeners = CopyOnWriteArrayList<() -> Unit>()

        /** Runs inside [subscribe] before the listener is added: the race window. */
        var beforeRegister: () -> Unit = {}

        /** Runs inside [subscribe] after the listener is added but before the handle is returned. */
        var afterRegister: () -> Unit = {}

        override fun isRunning() = running

        override fun subscribe(onRunning: () -> Unit): () -> Unit {
            beforeRegister()
            listeners += onRunning
            afterRegister()
            return { listeners.remove(onRunning) }
        }

        fun reachRunning() {
            running = true
            listeners.forEach { it() }
        }

        fun stop() {
            running = false
        }
    }

    @Test
    fun `already running - runs the action immediately without subscribing`() {
        val server = FakeServer(running = true)
        val runs = AtomicInteger()

        ServerReadinessGate(server).runWhenRunning { runs.incrementAndGet() }

        assertEquals(1, runs.get())
        assertTrue(server.listeners.isEmpty())
    }

    @Test
    fun `running arrives after registration - runs once and unsubscribes`() {
        val server = FakeServer()
        val runs = AtomicInteger()

        ServerReadinessGate(server).runWhenRunning { runs.incrementAndGet() }
        assertEquals(0, runs.get())

        server.reachRunning()
        assertEquals(1, runs.get())
        assertTrue(server.listeners.isEmpty(), "listener must be disposed once it has fired")

        // A later restart reaching Running again must not re-run a one-shot action.
        server.stop()
        server.reachRunning()
        assertEquals(1, runs.get())
    }

    @Test
    fun `running lands between the state check and registration - still runs once`() {
        val server = FakeServer()
        // The transition completes (and notifies its then-empty listener list) after the gate saw
        // "not running" but before its listener is registered.
        server.beforeRegister = { server.beforeRegister = {}; server.reachRunning() }
        val runs = AtomicInteger()

        ServerReadinessGate(server).runWhenRunning { runs.incrementAndGet() }

        assertEquals(1, runs.get())
        assertTrue(server.listeners.isEmpty(), "listener must not be left subscribed")
    }

    @Test
    fun `running notified during registration - runs once and unsubscribes`() {
        val server = FakeServer()
        // Notified on the platform's thread after the listener is in place, before subscribe() has
        // even returned its handle — the re-check after registration must not run the action again.
        server.afterRegister = { server.afterRegister = {}; server.reachRunning() }
        val runs = AtomicInteger()

        ServerReadinessGate(server).runWhenRunning { runs.incrementAndGet() }

        assertEquals(1, runs.get())
        assertTrue(server.listeners.isEmpty(), "listener must not be left subscribed")
    }

    @Test
    fun `N coalesced requests while starting produce one action and one listener`() {
        val server = FakeServer()
        val gate = ServerReadinessGate(server)
        val runs = AtomicInteger()

        repeat(5) { gate.runWhenRunning("baseline") { runs.incrementAndGet() } }
        assertEquals(1, server.listeners.size)

        server.reachRunning()
        assertEquals(1, runs.get())
        assertTrue(server.listeners.isEmpty())
    }

    @Test
    fun `a coalesced key is released once its action has run`() {
        val server = FakeServer()
        val gate = ServerReadinessGate(server)
        val runs = AtomicInteger()

        gate.runWhenRunning("baseline") { runs.incrementAndGet() }
        server.reachRunning()
        server.stop()

        // A later startup (e.g. after a server restart) gets its own deferred action.
        gate.runWhenRunning("baseline") { runs.incrementAndGet() }
        server.reachRunning()

        assertEquals(2, runs.get())
    }

    @Test
    fun `a key is released when registration throws`() {
        val server = FakeServer()
        server.beforeRegister = { server.beforeRegister = {}; throw IllegalStateException("parent disposed") }
        val gate = ServerReadinessGate(server)
        val runs = AtomicInteger()

        assertFailsWith<IllegalStateException> { gate.runWhenRunning("baseline") { runs.incrementAndGet() } }

        gate.runWhenRunning("baseline") { runs.incrementAndGet() }
        server.reachRunning()

        assertEquals(1, runs.get())
    }

    @Test
    fun `un-keyed requests are not coalesced`() {
        val server = FakeServer()
        val gate = ServerReadinessGate(server)
        val runs = AtomicInteger()

        repeat(3) { gate.runWhenRunning { runs.incrementAndGet() } }
        server.reachRunning()

        assertEquals(3, runs.get())
        assertTrue(server.listeners.isEmpty())
    }

    @Test
    fun `different keys are coalesced independently`() {
        val server = FakeServer()
        val gate = ServerReadinessGate(server)
        val a = AtomicInteger()
        val b = AtomicInteger()

        repeat(2) { gate.runWhenRunning("a") { a.incrementAndGet() } }
        repeat(2) { gate.runWhenRunning("b") { b.incrementAndGet() } }
        server.reachRunning()

        assertEquals(1, a.get())
        assertEquals(1, b.get())
    }

    @Test
    fun `concurrent requests racing the Running transition each run exactly once`() {
        repeat(1000) { iteration ->
            val server = FakeServer()
            val gate = ServerReadinessGate(server)
            val runs = AtomicInteger()
            val start = CountDownLatch(1)

            val requesters = (1..4).map {
                thread { start.await(); gate.runWhenRunning { runs.incrementAndGet() } }
            }
            val starter = thread { start.await(); server.reachRunning() }
            start.countDown()
            (requesters + starter).forEach { it.join(TimeUnit.SECONDS.toMillis(10)) }

            assertEquals(4, runs.get(), "iteration $iteration")
            assertTrue(server.listeners.isEmpty(), "iteration $iteration: listener left subscribed")
        }
    }
}
