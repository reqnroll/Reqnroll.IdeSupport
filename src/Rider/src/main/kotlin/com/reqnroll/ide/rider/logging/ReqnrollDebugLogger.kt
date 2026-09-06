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
 *
 * The line shape (issue #626) is the portable subset of the canonical format shared with the
 * .NET side's `LogLineFormatter` — UTC timestamp, a level padded to a fixed width, then the
 * message. The .NET format also carries a managed-thread-id segment; that's specific to the LSP
 * server's multi-threaded handler model (added to help diagnose issue #554) and has no
 * equivalent here. A per-call "source" segment is likewise not attempted: C# gets that for free
 * from the compiler's `[CallerFilePath]`, which Kotlin has no equivalent of, and this file's ~30
 * call sites already hand-write their originating class name into the message text itself (e.g.
 * `"ReqnrollFeatureInlayHintsController: ..."`) — good enough that a structured field isn't worth
 * a mechanical rewrite of every call site.
 */
object ReqnrollDebugLogger {
    private const val LEVEL_FIELD_WIDTH = 7 // width of "Warning" - the longest level name used here
    private val timestampFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC)
    private val fileDateFormatter = DateTimeFormatter.ofPattern("yyyyMMdd").withZone(ZoneOffset.UTC)
    private val logFile: File by lazy { resolveLogFile() }

    fun info(message: String) = log("Info", message, null)
    fun warn(message: String, throwable: Throwable? = null) = log("Warning", message, throwable)
    fun error(message: String, throwable: Throwable? = null) = log("Error", message, throwable)

    /** Renders the UTC timestamp prefix for a log line. Exposed for testing without mocking the system clock. */
    internal fun formatTimestamp(instant: Instant): String = timestampFormatter.format(instant)

    /** Renders one log line's full preamble + message, excluding the trailing newline. Exposed for testing. */
    internal fun formatLine(instant: Instant, level: String, message: String): String =
        "${formatTimestamp(instant)} [${level.padEnd(LEVEL_FIELD_WIDTH)}] $message"

    @Synchronized
    private fun log(level: String, message: String, throwable: Throwable?) {
        try {
            logFile.parentFile?.mkdirs()
            val line = buildString {
                append(formatLine(Instant.now(), level, message))
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
