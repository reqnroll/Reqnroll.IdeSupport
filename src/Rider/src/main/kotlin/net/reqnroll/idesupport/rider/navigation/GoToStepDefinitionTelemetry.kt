package net.reqnroll.idesupport.rider.navigation

import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
import java.util.concurrent.atomic.AtomicLong

/**
 * Turns the server's `textDocument/definition` lookup event into `Command.GoToStepDefinition`, but
 * only for a lookup that follows an explicit Go To Definition ([GoToStepDefinitionActionListener], [ReqnrollGoToDefinitionAction]).
 *
 * The platform sends `textDocument/definition` on hover as well, so the lookup alone is not a
 * navigation. They [arm] this gate just before the platform action runs; the next
 * `Lookup.FindStepDefinitions` for `textDocument/definition` within [WINDOW_MILLIS] is then
 * reported with the same `LocationCount` the server computed, and the gate disarms.
 */
internal object GoToStepDefinitionTelemetry {
    internal const val WINDOW_MILLIS = 5_000L
    private const val LOCATION_COUNT_PROPERTY = "LocationCount"
    private const val PROTOCOL_PROPERTY = "Protocol"
    private const val DEFINITION_PROTOCOL = "textDocument/definition"

    private val armedAt = AtomicLong(NOT_ARMED)

    fun arm(nowMillis: Long = System.currentTimeMillis()) {
        armedAt.set(nowMillis)
    }

    /** Called for every forwarded server telemetry event; transmits the navigation when it matches the armed gate. */
    fun onServerEvent(
        eventName: String,
        properties: Map<String, Any?>,
        nowMillis: Long = System.currentTimeMillis(),
        transmit: (String, Map<String, Any?>) -> Unit = RiderTelemetryTransmitter::transmit,
    ) {
        if (eventName != RiderTelemetryTransmitter.LOOKUP_FIND_STEP_DEFINITIONS) return
        if (properties[PROTOCOL_PROPERTY] != DEFINITION_PROTOCOL) return

        val armed = armedAt.getAndSet(NOT_ARMED)
        if (armed == NOT_ARMED || nowMillis - armed > WINDOW_MILLIS) return

        val count = (properties[LOCATION_COUNT_PROPERTY] as? Number)?.toInt() ?: 0
        transmit(RiderTelemetryTransmitter.COMMAND_GO_TO_STEP_DEFINITION, mapOf(LOCATION_COUNT_PROPERTY to count))
    }

    internal fun reset() = armedAt.set(NOT_ARMED)

    private const val NOT_ARMED = Long.MIN_VALUE
}
