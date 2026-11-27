package net.reqnroll.idesupport.rider.telemetry

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
    fun `first start sends installed and seeds state without a usage day`() {
        sut.reportInstallOrUpgrade(day1, "1.2.0")

        assertEquals(listOf("Lifecycle.Extension.Installed"), names())
        assertEquals("1.2.0", values[ExtensionLifecycleTelemetry.INSTALLED_VERSION_KEY])
        assertEquals("2026-10-03", values[ExtensionLifecycleTelemetry.LAST_USED_DATE_KEY])
        assertEquals("0", values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `use on the install day sends only loaded`() {
        sut.reportInstallOrUpgrade(day1, "1.2.0")
        sent.clear()

        sut.reportUse(day1)

        assertEquals(listOf("Lifecycle.Extension.Loaded"), names())
        assertEquals("0", values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `same version sends nothing at startup`() {
        sut.reportInstallOrUpgrade(day1, "1.2.0")
        sent.clear()

        sut.reportInstallOrUpgrade(day1.plusDays(1), "1.2.0")

        assertEquals(emptyList(), names())
        assertEquals("0", values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `a new day counts a usage day only when use is reported`() {
        sut.reportInstallOrUpgrade(day1, "1.2.0")
        sent.clear()

        sut.reportUse(day1.plusDays(1))
        sut.reportUse(day1.plusDays(1))
        sut.reportUse(day1.plusDays(2))

        assertEquals(
            listOf(
                "Lifecycle.Extension.Loaded", "Lifecycle.Usage.Recorded",
                "Lifecycle.Extension.Loaded",
                "Lifecycle.Extension.Loaded", "Lifecycle.Usage.Recorded",
            ),
            names(),
        )
        assertEquals("2", values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `a version increase sends upgraded with the old version and records the new one`() {
        sut.reportInstallOrUpgrade(day1, "1.2.0")
        sent.clear()

        sut.reportInstallOrUpgrade(day1, "1.10.0")

        assertEquals(listOf("Lifecycle.Extension.Upgraded"), names())
        assertEquals(mapOf<String, Any?>("OldExtensionVersion" to "1.2.0"), sent[0].second)
        assertEquals("1.10.0", values[ExtensionLifecycleTelemetry.INSTALLED_VERSION_KEY])
    }

    @Test
    fun `a downgrade sends no upgraded event`() {
        sut.reportInstallOrUpgrade(day1, "1.3.0")
        sent.clear()

        sut.reportInstallOrUpgrade(day1, "1.2.0")

        assertEquals(emptyList(), names())
        assertEquals("1.3.0", values[ExtensionLifecycleTelemetry.INSTALLED_VERSION_KEY])
    }

    @Test
    fun `use before any install sends only loaded`() {
        sut.reportUse(day1)

        assertEquals(listOf("Lifecycle.Extension.Loaded"), names())
        assertEquals(null, values[ExtensionLifecycleTelemetry.USAGE_DAYS_KEY])
    }

    @Test
    fun `versions compare numerically`() {
        assertTrue(ExtensionLifecycleTelemetry.compareVersions("1.2.0", "1.10.0") < 0)
        assertEquals(0, ExtensionLifecycleTelemetry.compareVersions("1.10", "1.10.0"))
        assertTrue(ExtensionLifecycleTelemetry.compareVersions("2.0.0", "1.99.99") > 0)
    }
}
