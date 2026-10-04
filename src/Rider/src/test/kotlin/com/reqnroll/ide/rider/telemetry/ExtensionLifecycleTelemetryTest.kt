package com.reqnroll.ide.rider.telemetry

import java.time.LocalDate
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ExtensionLifecycleTelemetryTest {
    private val values = mutableMapOf<String, String>()
    private val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
    private val sut = ExtensionLifecycleTelemetry(
        object : LifecycleStore {
            override fun get(key: String) = values[key]
            override fun set(key: String, value: String) {
                values[key] = value
            }
        },
    ) { name, props -> sent += name to props }

    private val day1 = LocalDate.of(2026, 10, 3)

    private fun names() = sent.map { it.first }

    @Test
    fun `first start sends loaded and installed and seeds state without a usage day`() {
        sut.report(day1, "1.2.0")

        assertEquals(listOf("Extension loaded", "Extension installed"), names())
        assertEquals("1.2.0", values[ExtensionLifecycleTelemetry.INSTALLED_VERSION_KEY])
        assertEquals("2026-10-03", values[ExtensionLifecycleTelemetry.LAST_USED_DATE_KEY])
        assertEquals("0", values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `same day same version sends only loaded`() {
        sut.report(day1, "1.2.0")
        sent.clear()

        sut.report(day1, "1.2.0")

        assertEquals(listOf("Extension loaded"), names())
    }

    @Test
    fun `a new day sends the usage heartbeat with the incremented count in the name`() {
        sut.report(day1, "1.2.0")
        sent.clear()

        sut.report(day1.plusDays(1), "1.2.0")
        sut.report(day1.plusDays(2), "1.2.0")

        assertEquals(listOf("Extension loaded", "1 day usage", "Extension loaded", "2 day usage"), names())
    }

    @Test
    fun `a version increase sends upgraded with the old version and records the new one`() {
        sut.report(day1, "1.2.0")
        sent.clear()

        sut.report(day1, "1.10.0")

        assertEquals(listOf("Extension loaded", "Extension upgraded"), names())
        assertEquals(mapOf<String, Any?>("OldExtensionVersion" to "1.2.0"), sent[1].second)
        assertEquals("1.10.0", values[ExtensionLifecycleTelemetry.INSTALLED_VERSION_KEY])
    }

    @Test
    fun `a downgrade sends no upgraded event`() {
        sut.report(day1, "1.3.0")
        sent.clear()

        sut.report(day1, "1.2.0")

        assertEquals(listOf("Extension loaded"), names())
    }

    @Test
    fun `versions compare numerically`() {
        assertTrue(ExtensionLifecycleTelemetry.compareVersions("1.2.0", "1.10.0") < 0)
        assertEquals(0, ExtensionLifecycleTelemetry.compareVersions("1.10", "1.10.0"))
        assertTrue(ExtensionLifecycleTelemetry.compareVersions("2.0.0", "1.99.99") > 0)
    }
}
