package net.reqnroll.idesupport.rider.telemetry

import com.google.gson.Gson
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import java.io.File
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * Debug-only sink that mirrors telemetry events to a local JSONL file for later review (issue
 * #799), giving Rider the same local mirror VS's `TelemetryTransmitter`/`TelemetryDebugLog.cs`
 * and VS Code's `telemetry.ts`/`telemetryDebugLog.ts` already have. Deliberately independent of
 * the `REQNROLL_TELEMETRY_ENABLED` opt-out gate — [RiderTelemetryTransmitter] records what it
 * *attempted* to send even when transmission is disabled or fails.
 */
interface TelemetryDebugLog {
    /**
     * Appends one record describing a telemetry event.
     * @param source Where the event was captured: `"server"` or `"host"`.
     * @param enabled Host only: whether the opt-out gate allowed transmission.
     * @param transmitted Host only: whether the event was actually handed off for transmission.
     * @param error Host only: the message if transmission threw, otherwise null.
     */
    fun record(
        source: String,
        eventName: String,
        properties: Map<String, Any?>? = null,
        enabled: Boolean? = null,
        transmitted: Boolean? = null,
        error: String? = null,
    )
}

object NullTelemetryDebugLog : TelemetryDebugLog {
    override fun record(
        source: String,
        eventName: String,
        properties: Map<String, Any?>?,
        enabled: Boolean?,
        transmitted: Boolean?,
        error: String?,
    ) {
        // No sink configured.
    }
}

/** Appends one JSON-line record describing the telemetry event to [path]. */
class FileTelemetryDebugLog(private val path: File) : TelemetryDebugLog {
    private val gson = Gson()

    override fun record(
        source: String,
        eventName: String,
        properties: Map<String, Any?>?,
        enabled: Boolean?,
        transmitted: Boolean?,
        error: String?,
    ) {
        try {
            val record = linkedMapOf<String, Any?>(
                "ts" to Instant.now().toString(),
                "source" to source,
                "event" to eventName,
                "props" to properties,
                "enabled" to enabled,
                "transmitted" to transmitted,
                "error" to error,
            )
            path.parentFile?.mkdirs()
            path.appendText(gson.toJson(record) + System.lineSeparator())
        } catch (_: Exception) {
            // Debug logging must never break the plugin.
        }
    }
}

/**
 * Resolves the [TelemetryDebugLog] sink from the `REQNROLL_TELEMETRY_DEBUG_LOG` environment
 * variable, matching the .NET side's `TelemetryDebugLog.FromEnvironment`/`FromValue` and VS
 * Code's `createTelemetryDebugLogFromEnvironment`/`createTelemetryDebugLogFromValue`: unset /
 * empty / `"0"` / `"false"` → disabled; `"1"` / `"true"` → JSONL at [defaultPath]; any other
 * value → treated as the target file path.
 */
object RiderTelemetryDebugLog {
    internal const val ENV_VAR = "REQNROLL_TELEMETRY_DEBUG_LOG"
    private val fileDateFormatter = DateTimeFormatter.ofPattern("yyyyMMdd").withZone(ZoneOffset.UTC)

    fun fromEnvironment(): TelemetryDebugLog = fromValue(System.getenv(ENV_VAR))

    /** Exposed for testing without mutating real environment state. */
    internal fun fromValue(value: String?): TelemetryDebugLog {
        val trimmed = value?.trim()
        if (trimmed.isNullOrEmpty()) return NullTelemetryDebugLog
        if (trimmed == "0" || trimmed.equals("false", ignoreCase = true)) return NullTelemetryDebugLog

        val path = if (trimmed == "1" || trimmed.equals("true", ignoreCase = true)) defaultPath() else File(trimmed)
        return FileTelemetryDebugLog(path)
    }

    /**
     * `<Reqnroll log dir>/reqnroll-telemetry-{yyyyMMdd}.jsonl` (UTC date) — the same file name
     * the .NET side's `TelemetryDebugLog.DefaultPath` and VS Code's `defaultTelemetryDebugLogPath`
     * resolve to, in the same shared [ReqnrollDebugLogger.logDirectory] this plugin's own general
     * log file lives under, so all three IDEs plus the LSP server can share one file.
     */
    internal fun defaultPath(): File {
        val dir = ReqnrollDebugLogger.logDirectory(
            System.getProperty("os.name"),
            System.getenv("LOCALAPPDATA"),
            System.getProperty("user.home"),
        )
        val date = fileDateFormatter.format(Instant.now())
        return File(dir, "reqnroll-telemetry-$date.jsonl")
    }
}
