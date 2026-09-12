package com.reqnroll.ide.rider.telemetry

import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.application.ApplicationInfo
import com.intellij.openapi.extensions.PluginId
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.time.Instant
import java.util.UUID

/**
 * Transmits `telemetry/event` notifications forwarded by [ReqnrollTelemetryEventInterceptor] to
 * Application Insights, giving Rider parity with VS
 * ([TelemetryEventInterceptor][com.reqnroll.ide.rider.telemetry] equivalent:
 * `Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception.TelemetryEventInterceptor` +
 * `TelemetryTransmitter.cs`) and VS Code (`src/VSCode/src/telemetry.ts`), which both already
 * forward every server-emitted event to this same Application Insights resource — Rider was the
 * one client with no consumer for `telemetry/event` at all (see issue #255).
 *
 * Posts directly to the public Application Insights ingestion REST endpoint via the JDK's built-in
 * `java.net.http.HttpClient` rather than pulling in the (Java, not Kotlin/Gradle-friendly)
 * Application Insights SDK — the wire format is small and stable
 * (https://learn.microsoft.com/azure/azure-monitor/app/api-custom-events-metrics), so a dependency
 * wasn't justified for one event type.
 */
object RiderTelemetryTransmitter {
    // Same Application Insights resource VS's TelemetryTransmitter.cs and VS Code's telemetry.ts
    // use (see src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/Telemetry/InstrumentationKey.txt),
    // so usage events from every Reqnroll IDE client land in the same place.
    private const val INSTRUMENTATION_KEY = "3fd018ff-819d-4685-a6e1-6f09bc98d20b"
    private const val INGESTION_ENDPOINT = "https://dc.services.visualstudio.com/v2/track"

    // Same env-var opt-out contract as Reqnroll.IdeSupport.Common.Telemetry.EnableTelemetryChecker
    // (used by VS's transmitter) — kept identical rather than inventing a Rider-specific setting,
    // since REQNROLL_TELEMETRY_ENABLED is meant to be a single cross-IDE kill switch.
    internal const val TELEMETRY_ENV_VAR = "REQNROLL_TELEMETRY_ENABLED"

    private val PLUGIN_ID = PluginId.getId("com.reqnroll.idesupport")
    private const val USER_ID_PROPERTY_KEY = "com.reqnroll.idesupport.telemetry.userId"

    private val httpClient: HttpClient by lazy { HttpClient.newHttpClient() }

    /** Transmits [eventName]/[properties] to Application Insights unless telemetry is disabled. */
    fun transmit(eventName: String, properties: Map<String, Any?>) {
        if (!isEnabled(System.getenv(TELEMETRY_ENV_VAR))) {
            ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: telemetry disabled; dropping $eventName")
            return
        }

        try {
            val stringProps = LinkedHashMap<String, String>()
            properties.forEach { (key, value) -> if (value != null) stringProps[key] = value.toString() }
            stringProps["Ide"] = "JetBrains Rider"
            stringProps["IdeVersion"] = ideVersion()
            stringProps["ExtensionVersion"] = extensionVersion()

            val body = buildEnvelope(eventName, userId(), stringProps, Instant.now())
            val request = HttpRequest.newBuilder()
                .uri(URI.create(INGESTION_ENDPOINT))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build()

            httpClient.sendAsync(request, HttpResponse.BodyHandlers.discarding())
                .exceptionally { ex ->
                    ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: failed to send $eventName", ex)
                    null
                }
        } catch (ex: Exception) {
            // A telemetry failure must never break the plugin — same posture as VS's
            // TelemetryTransmitter.TransmitEvent catch-all.
            ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: error preparing $eventName", ex)
        }
    }

    private fun userId(): String {
        val store = PropertiesComponent.getInstance()
        return store.getValue(USER_ID_PROPERTY_KEY) ?: UUID.randomUUID().toString().also {
            store.setValue(USER_ID_PROPERTY_KEY, it)
        }
    }

    private fun ideVersion(): String = ApplicationInfo.getInstance().fullVersion

    private fun extensionVersion(): String = PluginManagerCore.getPlugin(PLUGIN_ID)?.version ?: "unknown"

    /**
     * Pure/parameterized so the opt-out check is testable without mutating real environment state
     * — mirrors [com.reqnroll.ide.rider.lsp.ReqnrollServerPathResolver]'s rid/isWindows rationale.
     */
    internal fun isEnabled(envValue: String?): Boolean = envValue == null || envValue == "1"

    /**
     * Pure/parameterized Application Insights `TrackEvent` envelope builder — see
     * https://learn.microsoft.com/azure/azure-monitor/app/api-custom-events-metrics#event-telemetry.
     * `properties` values are pre-stringified by the caller (Application Insights properties are
     * always strings), matching VS's `TelemetryTransmitter.TransmitEvent`.
     */
    internal fun buildEnvelope(
        eventName: String,
        userId: String,
        properties: Map<String, String>,
        timestamp: Instant,
    ): String {
        val propsJson = properties.entries.joinToString(",") { (key, value) -> "${jsonString(key)}:${jsonString(value)}" }
        return """
            {"name":"Microsoft.ApplicationInsights.Event","time":"$timestamp","iKey":"$INSTRUMENTATION_KEY","tags":{"ai.user.id":${jsonString(userId)}},"data":{"baseType":"EventData","baseData":{"ver":2,"name":${jsonString(eventName)},"properties":{$propsJson}}}}
        """.trimIndent()
    }

    private fun jsonString(value: String): String {
        val escaped = buildString {
            for (c in value) {
                when (c) {
                    '\\' -> append("\\\\")
                    '"' -> append("\\\"")
                    '\n' -> append("\\n")
                    '\r' -> append("\\r")
                    '\t' -> append("\\t")
                    else -> if (c.code < 0x20) append("\\u%04x".format(c.code)) else append(c)
                }
            }
        }
        return "\"$escaped\""
    }
}
