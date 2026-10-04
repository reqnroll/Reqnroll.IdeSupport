package com.reqnroll.ide.rider.telemetry

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URI
import java.time.Duration
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Issue #859: an unreachable analytics endpoint must be silent, bounded and reported once. */
class TelemetryUnreachableEndpointTest {
    private companion object {
        val FAST_RETRIES = RiderTelemetryTransmitter.RetryPolicy(3, Duration.ofMillis(10), Duration.ofMillis(50))
    }

    private class Harness {
        val notices = mutableListOf<String>()
        val breaker = TelemetryCircuitBreaker(onFirstFailure = { notices.add(it) })
        val client = RiderTelemetryTransmitter.newHttpClient()
        val failures = mutableListOf<String>()

        /** Posts and waits for the failure callback; returns whether one arrived in time. */
        fun postAndAwait(uri: String, timeout: Duration = Duration.ofSeconds(1)): Boolean {
            val latch = CountDownLatch(1)
            RiderTelemetryTransmitter.post(client, URI.create(uri), "{}", breaker, timeout, FAST_RETRIES) { failures.add(it); latch.countDown() }
            return latch.await(10, TimeUnit.SECONDS)
        }
    }

    @Test
    fun `unresolvable host is a handled failure that opens the breaker`() {
        val h = Harness()
        assertTrue(h.postAndAwait("https://telemetry.invalid/v2/track"))
        assertTrue(h.breaker.isOpen)
        assertEquals(listOf(TelemetryCircuitBreaker.UNAVAILABLE_NOTICE), h.notices)
    }

    @Test
    fun `refused connection is a handled failure`() {
        val port = ServerSocket(0).use { it.localPort } // closed again => nothing listening
        val h = Harness()
        assertTrue(h.postAndAwait("http://127.0.0.1:$port/v2/track"))
        assertTrue(h.breaker.isOpen)
    }

    @Test
    fun `403 and 407 responses count as failures`() {
        for (status in listOf(403, 407)) {
            val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
            server.createContext("/") { ex -> ex.sendResponseHeaders(status, -1); ex.close() }
            server.start()
            try {
                val h = Harness()
                assertTrue(h.postAndAwait("http://127.0.0.1:${server.address.port}/"))
                assertEquals(listOf("HTTP $status"), h.failures)
                assertTrue(h.breaker.isOpen)
            } finally {
                server.stop(0)
            }
        }
    }

    @Test
    fun `success does not open the breaker`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { ex -> ex.sendResponseHeaders(200, -1); ex.close() }
        server.start()
        try {
            val h = Harness()
            val done = CountDownLatch(1)
            RiderTelemetryTransmitter.post(h.client, URI.create("http://127.0.0.1:${server.address.port}/"), "{}", h.breaker) { done.countDown() }
            assertFalse(done.await(1, TimeUnit.SECONDS))
            assertFalse(h.breaker.isOpen)
            assertTrue(h.notices.isEmpty())
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `black-holed endpoint times out instead of hanging`() {
        ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress()).use { blackHole ->
            // Accepts connections (backlog) but never answers.
            val h = Harness()
            val started = System.nanoTime()
            assertTrue(h.postAndAwait("http://127.0.0.1:${blackHole.localPort}/", Duration.ofMillis(500)))
            assertTrue(Duration.ofNanos(System.nanoTime() - started) < Duration.ofSeconds(8))
            assertTrue(h.breaker.isOpen)
        }
    }

    @Test
    fun `breaker emits the notice once however many failures follow`() {
        val notices = mutableListOf<String>()
        val verbose = mutableListOf<String>()
        val breaker = TelemetryCircuitBreaker({ notices.add(it) }, { verbose.add(it) })

        repeat(50) { breaker.recordFailure("boom") }

        assertEquals(listOf(TelemetryCircuitBreaker.UNAVAILABLE_NOTICE), notices)
        assertEquals(50, verbose.size)
        assertEquals(
            "Telemetry endpoint unreachable; telemetry for this session will be dropped",
            TelemetryCircuitBreaker.UNAVAILABLE_NOTICE,
        )
    }
}
