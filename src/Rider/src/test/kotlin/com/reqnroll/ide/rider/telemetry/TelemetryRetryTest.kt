package com.reqnroll.ide.rider.telemetry

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import java.net.URI
import java.time.Duration
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Transient delivery failures (transport errors, 408, 429, 5xx) are retried; everything else is final. */
class TelemetryRetryTest {
    private val fast = RiderTelemetryTransmitter.RetryPolicy(3, Duration.ofMillis(10), Duration.ofMillis(50))

    /** Serves [responses] in order (the last repeats); each is an action on the exchange. */
    private fun withServer(vararg responses: (HttpExchange) -> Unit, block: (url: String, requests: AtomicInteger) -> Unit) {
        val requests = AtomicInteger()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { ex ->
            val action = responses[minOf(requests.getAndIncrement(), responses.size - 1)]
            ex.requestBody.readAllBytes()
            action(ex)
        }
        server.start()
        try {
            block("http://127.0.0.1:${server.address.port}/", requests)
        } finally {
            server.stop(0)
        }
    }

    private fun status(code: Int, retryAfter: String? = null): (HttpExchange) -> Unit = { ex ->
        retryAfter?.let { ex.responseHeaders.add("Retry-After", it) }
        ex.sendResponseHeaders(code, -1)
        ex.close()
    }

    /** Closes the connection without a response, as an overloaded or recycling front end would. */
    private val dropConnection: (HttpExchange) -> Unit = { ex -> ex.close() }

    private class Outcome(val breaker: TelemetryCircuitBreaker = TelemetryCircuitBreaker(onFirstFailure = {})) {
        val failures = mutableListOf<String>()
        val failed = CountDownLatch(1)
    }

    private fun post(url: String, outcome: Outcome) {
        RiderTelemetryTransmitter.post(
            RiderTelemetryTransmitter.newHttpClient(), URI.create(url), "{}", outcome.breaker, Duration.ofSeconds(2), fast,
        ) { outcome.failures.add(it); outcome.failed.countDown() }
    }

    private fun awaitRequests(requests: AtomicInteger, expected: Int) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
        while (requests.get() < expected && System.nanoTime() < deadline) Thread.sleep(10)
    }

    @Test
    fun `a retryable status is retried and a later success leaves the breaker closed`() {
        for (code in listOf(408, 429, 500, 502, 503, 504)) {
            withServer(status(code), status(200)) { url, requests ->
                val outcome = Outcome()
                post(url, outcome)
                awaitRequests(requests, 2)
                assertFalse(outcome.failed.await(300, TimeUnit.MILLISECONDS), "status $code")
                assertEquals(2, requests.get(), "status $code")
                assertFalse(outcome.breaker.isOpen, "status $code")
            }
        }
    }

    @Test
    fun `a dropped connection is retried`() {
        withServer(dropConnection, status(200)) { url, requests ->
            val outcome = Outcome()
            post(url, outcome)
            awaitRequests(requests, 2)
            assertFalse(outcome.failed.await(300, TimeUnit.MILLISECONDS))
            assertEquals(2, requests.get())
            assertFalse(outcome.breaker.isOpen)
        }
    }

    @Test
    fun `a persistent retryable failure stops after maxAttempts then opens the breaker`() {
        withServer(status(503)) { url, requests ->
            val outcome = Outcome()
            post(url, outcome)
            assertTrue(outcome.failed.await(10, TimeUnit.SECONDS))
            assertEquals(3, requests.get())
            assertEquals(listOf("HTTP 503"), outcome.failures)
            assertTrue(outcome.breaker.isOpen)
        }
    }

    @Test
    fun `non-retryable statuses fail on the first attempt`() {
        for (code in listOf(400, 401, 402, 403, 404, 407, 413)) {
            withServer(status(code)) { url, requests ->
                val outcome = Outcome()
                post(url, outcome)
                assertTrue(outcome.failed.await(10, TimeUnit.SECONDS), "status $code")
                assertEquals(1, requests.get(), "status $code")
                assertEquals(listOf("HTTP $code"), outcome.failures)
                assertTrue(outcome.breaker.isOpen)
            }
        }
    }

    @Test
    fun `an open breaker suppresses further retries`() {
        withServer(status(503)) { url, requests ->
            val outcome = Outcome()
            outcome.breaker.recordFailure("earlier failure")
            post(url, outcome)
            assertTrue(outcome.failed.await(10, TimeUnit.SECONDS))
            assertEquals(1, requests.get())
        }
    }

    @Test
    fun `isRetryableStatus matches the statuses Microsoft SDKs retry`() {
        for (code in listOf(408, 429, 500, 502, 503, 504, 599)) assertTrue(RiderTelemetryTransmitter.isRetryableStatus(code), "$code")
        for (code in listOf(200, 206, 307, 400, 401, 402, 403, 404, 405, 413)) assertFalse(RiderTelemetryTransmitter.isRetryableStatus(code), "$code")
    }

    @Test
    fun `retryDelay backs off exponentially up to the cap`() {
        val policy = RiderTelemetryTransmitter.RetryPolicy(5, Duration.ofSeconds(1), Duration.ofSeconds(5))
        assertEquals(Duration.ofSeconds(1), RiderTelemetryTransmitter.retryDelay(1, policy))
        assertEquals(Duration.ofSeconds(2), RiderTelemetryTransmitter.retryDelay(2, policy))
        assertEquals(Duration.ofSeconds(4), RiderTelemetryTransmitter.retryDelay(3, policy))
        assertEquals(Duration.ofSeconds(5), RiderTelemetryTransmitter.retryDelay(4, policy))
    }

    @Test
    fun `retryDelay honours Retry-After seconds, capped, and ignores unusable values`() {
        val policy = RiderTelemetryTransmitter.RetryPolicy(3, Duration.ofSeconds(1), Duration.ofSeconds(30))
        assertEquals(Duration.ofSeconds(7), RiderTelemetryTransmitter.retryDelay(1, policy, " 7 "))
        assertEquals(Duration.ofSeconds(30), RiderTelemetryTransmitter.retryDelay(1, policy, "3600"))
        assertEquals(Duration.ZERO, RiderTelemetryTransmitter.retryDelay(1, policy, "0"))
        for (bad in listOf("soon", "-5", "Wed, 21 Oct 2026 07:28:00 GMT", "")) {
            assertEquals(Duration.ofSeconds(1), RiderTelemetryTransmitter.retryDelay(1, policy, bad), bad)
        }
    }
}
