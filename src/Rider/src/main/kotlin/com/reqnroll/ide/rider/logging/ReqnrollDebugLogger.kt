package com.reqnroll.ide.rider.logging

import java.io.File
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * Client-side glue log, mirroring the VS extension's SynchronousFileLogger convention
 * (src/Core/Reqnroll.IdeSupport.Common/Logging/AsynchronousFileLogger.cs): plugin
 * lifecycle/diagnostic messages — not LSP wire traffic, see CONTRIBUTING.md for why that
 * part isn't replicable here — appended to
 * `<Reqnroll log dir>/reqnroll-rider-ext-<yyyyMMdd>-<pid>.log`, pruned after 10 days.
 * Log directory follows the VS Code extension's per-OS convention (lspInspectorLogger.ts
 * resolveLogDirectory), since this plugin runs on the JVM across the same OSes VS Code
 * does, unlike the Windows-only VS extension.
 *
 * Timestamps are UTC (issue #625) — the previous `LocalDateTime.now()` carried no offset at
 * all, so a log collected from a machine in an unknown timezone couldn't be correlated with
 * the UTC timestamps the LSP server and VS extension write to their own log files.
 */
object ReqnrollDebugLogger {
    private val timestampFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC)
    private val fileDateFormatter = DateTimeFormatter.ofPattern("yyyyMMdd").withZone(ZoneOffset.UTC)
    private val logFile: File by lazy { resolveLogFile() }

    fun info(message: String) = log("INFO", message, null)
    fun warn(message: String, throwable: Throwable? = null) = log("WARN", message, throwable)
    fun error(message: String, throwable: Throwable? = null) = log("ERROR", message, throwable)

    /** Renders the UTC timestamp prefix for a log line. Exposed for testing without mocking the system clock. */
    internal fun formatTimestamp(instant: Instant): String = timestampFormatter.format(instant)

    @Synchronized
    private fun log(level: String, message: String, throwable: Throwable?) {
        try {
            logFile.parentFile?.mkdirs()
            val timestamp = formatTimestamp(Instant.now())
            val line = buildString {
                append(timestamp).append(", ").append(level).append(": ").append(message)
                if (throwable != null) {
                    append("\n    : ").append(throwable.stackTraceToString().trimEnd().prependIndent("    "))
                }
                append(System.lineSeparator())
            }
            logFile.appendText(line)
        } catch (_: Exception) {
            // Best-effort — a logging failure must never break plugin behavior.
        }
    }

    private fun resolveLogFile(): File {
        val dir = resolveLogDirectory()
        pruneOldLogs(dir)
        val pid = ProcessHandle.current().pid()
        val date = fileDateFormatter.format(Instant.now())
        return File(dir, "reqnroll-rider-ext-$date-$pid.log")
    }

    private fun resolveLogDirectory(): File =
        logDirectory(System.getProperty("os.name"), System.getenv("LOCALAPPDATA"), System.getProperty("user.home"))

    /**
     * Pure function taking explicit `os.name`/`LOCALAPPDATA`/`user.home` values rather than
     * reading `System.getProperty`/`getenv` directly — lets [resolveLogDirectory]'s per-OS
     * selection be unit tested for every OS without mutating global JVM/environment state. Mirrors
     * [com.reqnroll.ide.rider.lsp.ReqnrollServerPathResolver]'s identical rationale for its own
     * `rid`/`isWindows` functions.
     */
    internal fun logDirectory(osName: String, localAppData: String?, home: String): File {
        val os = osName.lowercase()
        return when {
            os.contains("win") -> File(localAppData ?: home, "Reqnroll")
            os.contains("mac") -> File(home, "Library/Logs/Reqnroll")
            else -> File(home, ".local/share/Reqnroll")
        }
    }

    private fun pruneOldLogs(dir: File) {
        try {
            val cutoffMillis = System.currentTimeMillis() - 10L * 24 * 60 * 60 * 1000
            dir.listFiles { f -> f.name.startsWith("reqnroll-") && f.name.endsWith(".log") }
                ?.filter { it.lastModified() < cutoffMillis }
                ?.forEach { it.delete() }
        } catch (_: Exception) {
            // Best-effort.
        }
    }
}
