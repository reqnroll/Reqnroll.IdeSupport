package com.reqnroll.ide.rider.telemetry

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ReqnrollTelemetryEventInterceptorTest {
    @Test
    fun `parses a well-formed event with string properties`() {
        val raw = mapOf(
            "eventName" to "StepUsageFound",
            "properties" to mapOf("count" to 3, "client" to "rider"),
        )

        val result = parseTelemetryEvent(raw)

        assertEquals(ParsedTelemetryEvent("StepUsageFound", mapOf("count" to 3, "client" to "rider")), result)
    }

    @Test
    fun `returns null when eventName is missing`() {
        assertNull(parseTelemetryEvent(mapOf("properties" to mapOf("a" to "b"))))
    }

    @Test
    fun `returns null when eventName is blank`() {
        assertNull(parseTelemetryEvent(mapOf("eventName" to "")))
    }

    @Test
    fun `returns null when the raw payload is not a Map at all`() {
        assertNull(parseTelemetryEvent("not a map"))
    }

    @Test
    fun `returns empty properties when properties is missing`() {
        val result = parseTelemetryEvent(mapOf("eventName" to "NoProps"))

        assertEquals(ParsedTelemetryEvent("NoProps", emptyMap()), result)
    }

    @Test
    fun `returns empty properties when properties is present but not a Map`() {
        val result = parseTelemetryEvent(mapOf("eventName" to "BadProps", "properties" to "oops"))

        assertEquals(ParsedTelemetryEvent("BadProps", emptyMap()), result)
    }

    @Test
    fun `drops property entries whose key is not a String`() {
        val raw = mapOf(
            "eventName" to "MixedKeys",
            "properties" to mapOf("good" to 1, 2 to "bad-key-dropped"),
        )

        val result = parseTelemetryEvent(raw)

        assertEquals(ParsedTelemetryEvent("MixedKeys", mapOf("good" to 1)), result)
    }
}
