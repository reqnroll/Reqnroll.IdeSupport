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
import java.time.Duration
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

    /**
     * Client-originated telemetry event names — mirrors `TelemetryEvents.cs` in the shared
     * `Reqnroll.IdeSupport.Common` project (`src/Core/Reqnroll.IdeSupport.Common/Telemetry/TelemetryEvents.cs`)
     * and the VS Code copy in `src/VSCode/src/telemetryEvents.ts`. Server-originated events
     * (forwarded by [ReqnrollTelemetryEventInterceptor]) need no entries here. Keep the three
     * copies in sync.
     */
    internal const val GO_TO_HOOK_COMMAND_EXECUTED = "GoToHook command executed"

    // Server-lifecycle events (issue #845): sent by the client because a dead server cannot report
    // itself. `Reason` is always one of the SERVER_FAILURE_REASON_* values below, `AttemptNumber` the
    // 1-based start attempt in this IDE session. Mirror of ServerLifecycleTelemetry.cs.
    internal const val SERVER_START_FAILED = "ServerStartFailed"
    internal const val SERVER_EXITED_UNEXPECTEDLY = "ServerExitedUnexpectedly"
    internal const val SERVER_RESTARTED = "ServerRestarted"

    internal const val SERVER_FAILURE_REASON_START_FAILED = "StartFailed"
    internal const val SERVER_FAILURE_REASON_PROCESS_EXITED = "ProcessExited"
    internal const val SERVER_FAILURE_REASON_SESSION_ENDED = "SessionEnded"

    /**
     * `Source` property of [GO_TO_HOOK_COMMAND_EXECUTED] (issue #861): how the navigation was
     * started. Closed set shared with VS and VS Code (`GoToHookSources.cs` / `GoToHookSource` in
     * `telemetryEvents.ts`); keep the copies in sync. Event-scoped key: the LSP server's
     * `TelemetryProperties.Source` uses the same literal for a class name on `UnhandledException`.
     */
    internal const val GO_TO_HOOK_SOURCE_PROPERTY = "Source"
    internal const val GO_TO_HOOK_SOURCE_COMMAND = "Command"
    internal const val GO_TO_HOOK_SOURCE_CONTEXT_MENU = "ContextMenu"
    internal const val GO_TO_HOOK_SOURCE_CODE_LENS = "CodeLens"

    internal const val IDE_CLIENT = "rider"

    private val PLUGIN_ID = PluginId.getId("com.reqnroll.idesupport")
    private const val USER_ID_PROPERTY_KEY = "com.reqnroll.idesupport.telemetry.userId"

    // Bounded timeouts (#859): without them a black-holed endpoint leaves one pending request and
    // socket per event until the OS gives up.
    internal val CONNECT_TIMEOUT: Duration = Duration.ofSeconds(5)
    internal val REQUEST_TIMEOUT: Duration = Duration.ofSeconds(5)

    private val httpClient: HttpClient by lazy { newHttpClient() }

    internal fun newHttpClient(): HttpClient = HttpClient.newBuilder().connectTimeout(CONNECT_TIMEOUT).build()

    internal val breaker = TelemetryCircuitBreaker(
        onFirstFailure = { ReqnrollDebugLogger.info(it) },
        onSuppressedFailure = { ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: $it") },
    )

    /**
     * Transmits [eventName]/[properties] to Application Insights unless telemetry is disabled;
     * always mirrors the attempt (sent or not) to the local debug log (issue #799), matching VS's
     * `TelemetryTransmitter.TransmitEvent` and VS Code's `telemetry.ts#sendTelemetryEvent`.
     */
    fun transmit(eventName: String, properties: Map<String, Any?>) {
        val debugLog = RiderTelemetryDebugLog.fromEnvironment()
        val enabled = isEnabled(System.getenv(TELEMETRY_ENV_VAR))

        if (!enabled) {
            ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: telemetry disabled; dropping $eventName")
            debugLog.record("host", eventName, properties, enabled = false, transmitted = false)
            return
        }

        if (breaker.isOpen) {
            debugLog.record("host", eventName, properties, enabled = true, transmitted = false, error = "telemetry endpoint unreachable")
            return
        }

        try {
            val stringProps = LinkedHashMap<String, String>()
            properties.forEach { (key, value) -> if (value != null) stringProps[key] = value.toString() }
            stampClientIdentity(stringProps, ideVersion(), extensionVersion())

            val body = buildEnvelope(eventName, userId(), stringProps, Instant.now())
            post(httpClient, URI.create(INGESTION_ENDPOINT), body, breaker) { reason ->
                debugLog.record("host", eventName, properties, enabled = true, transmitted = false, error = reason)
            }

            debugLog.record("host", eventName, properties, enabled = true, transmitted = true)
        } catch (ex: Exception) {
            // A telemetry failure must never break the plugin — same posture as VS's
            // TelemetryTransmitter.TransmitEvent catch-all.
            ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: error preparing $eventName", ex)
            breaker.recordFailure(ex.message ?: ex.javaClass.simpleName)
            debugLog.record("host", eventName, properties, enabled = true, transmitted = false, error = ex.message)
        }
    }

    /**
     * Fire-and-forget POST of [body]; never throws. Any failure (DNS, refused/reset, TLS, timeout,
     * or a non-2xx status such as a proxy's 403/407) opens [breaker] and is reported to
     * [onFailure], so an unreachable endpoint costs at most the first in-flight requests (#859).
     */
    internal fun post(
        client: HttpClient,
        endpoint: URI,
        body: String,
        breaker: TelemetryCircuitBreaker,
        timeout: Duration = REQUEST_TIMEOUT,
        onFailure: (String) -> Unit = {},
    ) {
        try {
            val request = HttpRequest.newBuilder()
                .uri(endpoint)
                .timeout(timeout)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build()

            client.sendAsync(request, HttpResponse.BodyHandlers.discarding())
                .whenComplete { response, ex ->
                    val reason = when {
                        ex != null -> (ex.cause ?: ex).let { it.message ?: it.javaClass.simpleName }
                        response.statusCode() !in 200..299 -> "HTTP ${response.statusCode()}"
                        else -> null
                    }
                    if (reason != null) {
                        breaker.recordFailure(reason)
                        onFailure(reason)
                    }
                }
        } catch (ex: Exception) {
            // Synchronous failure (e.g. unresolvable host surfaced eagerly) -- same policy.
            val reason = ex.message ?: ex.javaClass.simpleName
            breaker.recordFailure(reason)
            onFailure(reason)
        }
    }

    /**
     * Stamps the host's client identity on every event (issue #844). `IdeClient` is the canonical
     * cross-IDE key, also stamped by the LSP server on server-originated events with the same
     * `visualstudio`/`vscode`/`rider` vocabulary; it is only added when absent so a server-stamped
     * value is never overridden, and covers host-originated events (GoToHook) the server never sees.
     */
    internal fun stampClientIdentity(
        properties: MutableMap<String, String>,
        ideVersion: String,
        extensionVersion: String,
    ) {
        properties.putIfAbsent("IdeClient", IDE_CLIENT)
        properties["Ide"] = "JetBrains Rider"
        properties["IdeVersion"] = ideVersion
        properties["ExtensionVersion"] = extensionVersion
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
