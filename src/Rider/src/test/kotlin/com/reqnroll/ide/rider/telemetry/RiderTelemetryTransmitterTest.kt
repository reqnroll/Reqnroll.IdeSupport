package com.reqnroll.ide.rider.telemetry

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RiderTelemetryTransmitterTest {
    @Test
    fun `isEnabled defaults to true when the env var is unset`() {
        assertTrue(RiderTelemetryTransmitter.isEnabled(null))
    }

    @Test
    fun `isEnabled is true only for the literal value 1`() {
        assertTrue(RiderTelemetryTransmitter.isEnabled("1"))
        assertFalse(RiderTelemetryTransmitter.isEnabled("0"))
        assertFalse(RiderTelemetryTransmitter.isEnabled("false"))
    }

    @Test
    fun `isBuiltInBlocked blocks only the dev sandbox without a usable override`() {
        assertTrue(RiderTelemetryTransmitter.isBuiltInBlocked(true, null))
        assertTrue(RiderTelemetryTransmitter.isBuiltInBlocked(true, "nonsense"))
        assertFalse(RiderTelemetryTransmitter.isBuiltInBlocked(true, "InstrumentationKey=abc"))
        assertFalse(RiderTelemetryTransmitter.isBuiltInBlocked(false, null))
    }

    @Test
    fun `resolveConnection falls back to the built-in connection when unset or blank`() {
        val builtIn = RiderTelemetryTransmitter.resolveConnection(null)
        val invalid = mutableListOf<String>()
        assertEquals(builtIn, RiderTelemetryTransmitter.resolveConnection("  ") { invalid.add(it) })
        assertTrue(invalid.isEmpty())
    }

    @Test
    fun `resolveConnection uses the key and appends v2 track to a custom ingestion endpoint`() {
        val c = RiderTelemetryTransmitter.resolveConnection(
            "InstrumentationKey=abc;IngestionEndpoint=https://localhost:1234/",
        )
        assertEquals("abc", c.instrumentationKey)
        assertEquals("https://localhost:1234/v2/track", c.endpoint)
    }

    @Test
    fun `resolveConnection keeps the default endpoint when only a key is given`() {
        val builtIn = RiderTelemetryTransmitter.resolveConnection(null)
        val c = RiderTelemetryTransmitter.resolveConnection("instrumentationkey=abc")
        assertEquals("abc", c.instrumentationKey)
        assertEquals(builtIn.endpoint, c.endpoint)
    }

    @Test
    fun `resolveConnection ignores and reports a value without an instrumentation key`() {
        val builtIn = RiderTelemetryTransmitter.resolveConnection(null)
        for (bad in listOf("nonsense", "InstrumentationKey=", "IngestionEndpoint=https://x/")) {
            val invalid = mutableListOf<String>()
            assertEquals(builtIn, RiderTelemetryTransmitter.resolveConnection(bad) { invalid.add(it) })
            assertEquals(1, invalid.size)
        }
    }

    @Test
    fun `buildEnvelope embeds the event name, user id, iKey and properties`() {
        val timestamp = Instant.parse("2026-07-20T12:00:00Z")
        val json = RiderTelemetryTransmitter.buildEnvelope(
            eventName = "GoToStepDefinition command executed",
            userId = "user-123",
            properties = mapOf("Ide" to "JetBrains Rider", "IdeVersion" to "2024.3.5"),
            timestamp = timestamp,
        )

        assertTrue(json.contains("\"name\":\"Microsoft.ApplicationInsights.Event\""))
        assertTrue(json.contains("\"iKey\":\"3fd018ff-819d-4685-a6e1-6f09bc98d20b\""))
        assertTrue(json.contains("\"ai.user.id\":\"user-123\""))
        assertTrue(json.contains("\"name\":\"GoToStepDefinition command executed\""))
        assertTrue(json.contains("\"Ide\":\"JetBrains Rider\""))
        assertTrue(json.contains("\"IdeVersion\":\"2024.3.5\""))
        assertTrue(json.contains("\"time\":\"$timestamp\""))
    }

    @Test
    fun `buildEnvelope carries a FeatureUsageSummary counts string as an escaped JSON string value`() {
        // The server sends per-kind counts as a compact JSON string (#582) because Rider's
        // value.toString() would render a nested Gson map as {key=1.0}.
        val json = RiderTelemetryTransmitter.buildEnvelope(
            eventName = "FeatureUsageSummary",
            userId = "user-123",
            properties = mapOf("LookupCounts" to """{"CodeAction":1,"Completion.Step":14}"""),
            timestamp = Instant.parse("2026-07-20T12:00:00Z"),
        )

        assertTrue(json.contains("\"LookupCounts\":\"{\\\"CodeAction\\\":1,\\\"Completion.Step\\\":14}\""))
    }

    @Test
    fun `stampClientIdentity adds the canonical IdeClient plus host version keys`() {
        val props = linkedMapOf("Operation" to "x")

        RiderTelemetryTransmitter.stampClientIdentity(props, "2024.3.5", "1.2.3")

        assertEquals("rider", props["IdeClient"])
        assertEquals("JetBrains Rider", props["Ide"])
        assertEquals("2024.3.5", props["IdeVersion"])
        assertEquals("1.2.3", props["ExtensionVersion"])
        assertEquals("x", props["Operation"])
    }

    @Test
    fun `stampClientIdentity does not override a server-stamped IdeClient`() {
        val props = linkedMapOf("IdeClient" to "explicit")

        RiderTelemetryTransmitter.stampClientIdentity(props, "2024.3.5", "1.2.3")

        assertEquals("explicit", props["IdeClient"])
    }

    @Test
    fun `buildEnvelope escapes special characters in property values`() {
        val json = RiderTelemetryTransmitter.buildEnvelope(
            eventName = "Test\nEvent \"quoted\"",
            userId = "user-123",
            properties = mapOf("Message" to "line1\nline2\t\"quoted\"\\backslash"),
            timestamp = Instant.parse("2026-07-20T12:00:00Z"),
        )

        assertEquals(
            "\"Test\\nEvent \\\"quoted\\\"\"",
            Regex("\"name\":(\"(?:[^\"\\\\]|\\\\.)*\"),\"properties\"").find(json)!!.groupValues[1],
        )
        assertTrue(json.contains("\\\"quoted\\\"\\\\backslash"))
        assertTrue(json.contains("line1\\nline2\\t"))
    }
}
