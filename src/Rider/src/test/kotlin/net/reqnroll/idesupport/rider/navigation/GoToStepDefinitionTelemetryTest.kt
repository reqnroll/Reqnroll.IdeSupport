package net.reqnroll.idesupport.rider.navigation

import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class GoToStepDefinitionTelemetryTest {
    private val sent = mutableListOf<Pair<String, Map<String, Any?>>>()
    private val transmit: (String, Map<String, Any?>) -> Unit = { name, props -> sent.add(name to props) }

    private fun lookup(count: Any? = 2.0, protocol: String = "textDocument/definition") =
        mapOf("LocationCount" to count, "Protocol" to protocol)

    private fun fire(properties: Map<String, Any?>, now: Long, name: String = "Lookup.FindStepDefinitions") =
        GoToStepDefinitionTelemetry.onServerEvent(name, properties, now, transmit)

    @AfterTest
    fun cleanUp() = GoToStepDefinitionTelemetry.reset()

    @Test
    fun `an armed definition lookup is reported as a navigation with the server's LocationCount`() {
        GoToStepDefinitionTelemetry.arm(1_000)
        fire(lookup(count = 2.0), 1_200)

        assertEquals(listOf("Command.GoToStepDefinition" to mapOf<String, Any?>("LocationCount" to 2)), sent)
    }

    @Test
    fun `a lookup that was not preceded by Go To Definition is not reported`() {
        fire(lookup(), 1_000)
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `only the first lookup after arming is reported`() {
        GoToStepDefinitionTelemetry.arm(1_000)
        fire(lookup(), 1_100)
        fire(lookup(), 1_200)
        assertEquals(1, sent.size)
    }

    @Test
    fun `a lookup after the window has elapsed is not reported`() {
        GoToStepDefinitionTelemetry.arm(1_000)
        fire(lookup(), 1_000 + GoToStepDefinitionTelemetry.WINDOW_MILLIS + 1)
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `other events and protocols leave the gate armed`() {
        GoToStepDefinitionTelemetry.arm(1_000)
        fire(lookup(), 1_100, name = "Lookup.FindStepUsages")
        fire(lookup(protocol = "reqnroll/findStepDefinitions"), 1_100)
        assertTrue(sent.isEmpty())

        fire(lookup(), 1_200)
        assertEquals(1, sent.size)
    }

    @Test
    fun `a missing LocationCount is reported as zero`() {
        GoToStepDefinitionTelemetry.arm(1_000)
        fire(mapOf("Protocol" to "textDocument/definition"), 1_100)
        assertEquals(mapOf<String, Any?>("LocationCount" to 0), sent.single().second)
    }
}
