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
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

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

    /** Developer override (issue #889), shared with VS and VS Code: an Application Insights connection string. */
    internal const val CONNECTION_STRING_ENV_VAR = "REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING"

    internal data class Connection(val instrumentationKey: String, val endpoint: String)

    private val BUILT_IN_CONNECTION = Connection(INSTRUMENTATION_KEY, INGESTION_ENDPOINT)

    /**
     * Pure/parameterized like [isEnabled]. Uses [envValue] when it carries a non-empty
     * `InstrumentationKey` (and, optionally, an `IngestionEndpoint`, to which `/v2/track` is
     * appended); anything else falls back to the built-in connection and is reported to [onInvalid].
     */
    internal fun resolveConnection(envValue: String?, onInvalid: (String) -> Unit = {}): Connection =
        parseOverride(envValue, onInvalid) ?: BUILT_IN_CONNECTION

    /**
     * Debug-build guard (issue #889): the `runIde` dev sandbox (`reqnroll.devSandbox`, see
     * build.gradle.kts) must not send to the built-in production resource, so without a usable
     * override it sends nothing.
     */
    internal fun isBuiltInBlocked(isDevSandbox: Boolean, envValue: String?): Boolean =
        isDevSandbox && parseOverride(envValue) == null

    private fun parseOverride(envValue: String?, onInvalid: (String) -> Unit = {}): Connection? {
        val value = envValue?.trim()
        if (value.isNullOrEmpty()) return null
        val parts = value.split(';').mapNotNull { part ->
            val idx = part.indexOf('=')
            if (idx > 0) part.substring(0, idx).trim().lowercase() to part.substring(idx + 1).trim() else null
        }.toMap()
        val key = parts["instrumentationkey"]
        if (key.isNullOrEmpty()) {
            onInvalid("$CONNECTION_STRING_ENV_VAR has no InstrumentationKey; ignoring it.")
            return null
        }
        val endpoint = parts["ingestionendpoint"]?.takeIf { it.isNotEmpty() }
            ?.let { it.trimEnd('/') + "/v2/track" } ?: INGESTION_ENDPOINT
        return Connection(key, endpoint)
    }

    private val connection: Connection by lazy {
        resolveConnection(System.getenv(CONNECTION_STRING_ENV_VAR)) { ReqnrollDebugLogger.info(it) }
    }

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

    /** Exception caught in the plugin's own code (issue #621); same event the server sends for its exceptions. See [ClientExceptionTelemetry]. */
    internal const val UNHANDLED_EXCEPTION = "UnhandledException"

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
        val enabled = isEnabled(System.getenv(TELEMETRY_ENV_VAR)) &&
            !isBuiltInBlocked(System.getProperty("reqnroll.devSandbox") == "true", System.getenv(CONNECTION_STRING_ENV_VAR))

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

            val body = buildEnvelope(eventName, userId(), stringProps, Instant.now(), connection.instrumentationKey)
            post(httpClient, URI.create(connection.endpoint), body, breaker) { reason ->
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
     * Retry policy for transient delivery failures: up to [maxAttempts] sends in total, waiting
     * `baseDelay * 2^(attempt-1)` between them (or the server's `Retry-After`), never longer than [maxDelay].
     */
    internal data class RetryPolicy(val maxAttempts: Int, val baseDelay: Duration, val maxDelay: Duration)

    internal val RETRY_POLICY = RetryPolicy(maxAttempts = 3, baseDelay = Duration.ofSeconds(1), maxDelay = Duration.ofSeconds(30))

    /** Statuses Microsoft's Application Insights SDKs retry: 408, 429 and 5xx. Others (400, 401, 402, 403, 404, 413...) are final. */
    internal fun isRetryableStatus(status: Int): Boolean = status == 408 || status == 429 || status in 500..599

    /** Wait before send number `attempt + 1`: the server's `Retry-After` seconds if valid, else exponential backoff; capped by [RetryPolicy.maxDelay]. */
    internal fun retryDelay(attempt: Int, policy: RetryPolicy, retryAfter: String? = null): Duration {
        val requested = retryAfter?.trim()?.toLongOrNull()?.takeIf { it >= 0 }?.let { Duration.ofSeconds(it) }
            ?: policy.baseDelay.multipliedBy(1L shl (attempt - 1).coerceIn(0, 20))
        return if (requested > policy.maxDelay) policy.maxDelay else requested
    }

    /**
     * Fire-and-forget POST of [body]; never throws. Transport failures (DNS, refused/reset, an HTTP/2
     * `GOAWAY`, TLS, timeout) and retryable statuses (see [isRetryableStatus]) are retried per
     * [retryPolicy]; once those are exhausted -- or for any non-retryable failure such as a proxy's
     * 403/407 -- [breaker] opens and [onFailure] is told, so an unreachable endpoint costs at most
     * the first in-flight requests (#859). No retry is scheduled once the breaker is open.
     */
    internal fun post(
        client: HttpClient,
        endpoint: URI,
        body: String,
        breaker: TelemetryCircuitBreaker,
        timeout: Duration = REQUEST_TIMEOUT,
        retryPolicy: RetryPolicy = RETRY_POLICY,
        onFailure: (String) -> Unit = {},
    ) {
        try {
            val request = HttpRequest.newBuilder()
                .uri(endpoint)
                .timeout(timeout)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build()

            send(client, request, breaker, retryPolicy, 1, onFailure)
        } catch (ex: Exception) {
            // Synchronous failure (e.g. unresolvable host surfaced eagerly) -- same policy.
            giveUp(ex.message ?: ex.javaClass.simpleName, breaker, onFailure)
        }
    }

    private fun giveUp(reason: String, breaker: TelemetryCircuitBreaker, onFailure: (String) -> Unit) {
        breaker.recordFailure(reason)
        onFailure(reason)
    }

    private fun send(
        client: HttpClient,
        request: HttpRequest,
        breaker: TelemetryCircuitBreaker,
        policy: RetryPolicy,
        attempt: Int,
        onFailure: (String) -> Unit,
    ) {
        client.sendAsync(request, HttpResponse.BodyHandlers.discarding())
            .whenComplete { response, ex ->
                val reason = when {
                    ex != null -> (ex.cause ?: ex).let { it.message ?: it.javaClass.simpleName }
                    response.statusCode() !in 200..299 -> "HTTP ${response.statusCode()}"
                    else -> return@whenComplete
                }
                val retryable = ex != null || isRetryableStatus(response.statusCode())
                if (!retryable || attempt >= policy.maxAttempts || breaker.isOpen) {
                    giveUp(reason, breaker, onFailure)
                    return@whenComplete
                }

                val delay = retryDelay(attempt, policy, response?.headers()?.firstValue("Retry-After")?.orElse(null))
                ReqnrollDebugLogger.verbose("RiderTelemetryTransmitter: attempt $attempt failed ($reason); retrying in ${delay.toMillis()} ms")
                CompletableFuture.delayedExecutor(delay.toMillis(), TimeUnit.MILLISECONDS).execute {
                    try {
                        send(client, request, breaker, policy, attempt + 1, onFailure)
                    } catch (e: Exception) {
                        giveUp(e.message ?: e.javaClass.simpleName, breaker, onFailure)
                    }
                }
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
        instrumentationKey: String = INSTRUMENTATION_KEY,
    ): String {
        val propsJson = properties.entries.joinToString(",") { (key, value) -> "${jsonString(key)}:${jsonString(value)}" }
        return """
            {"name":"Microsoft.ApplicationInsights.Event","time":"$timestamp","iKey":${jsonString(instrumentationKey)},"tags":{"ai.user.id":${jsonString(userId)}},"data":{"baseType":"EventData","baseData":{"ver":2,"name":${jsonString(eventName)},"properties":{$propsJson}}}}
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
