package com.reqnroll.ide.rider.logging

import java.io.File
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollDebugLoggerTest {
    @Test
    fun `formatTimestamp renders UTC with a Z suffix regardless of the JVM default timezone`() {
        // Instant has no timezone of its own; formatTimestamp must render it in UTC even if the
        // JVM's default timezone (java.util.TimeZone.getDefault) is something else entirely —
        // otherwise a log collected from a machine in an unknown timezone couldn't be correlated
        // with the UTC timestamps the LSP server and VS extension write (issue #625).
        assertEquals(
            "2026-09-06T14:02:11.123Z",
            ReqnrollDebugLogger.formatTimestamp(Instant.parse("2026-09-06T14:02:11.123Z")),
        )
    }

    @Test
    fun `formatLine pads the level to a fixed width and matches the dotnet side's shape`() {
        // Portable subset of Reqnroll.IdeSupport.Common.Logging.LogLineFormatter.FormatPreamble
        // (issue #626): "<UTC timestamp> [<level padded to 7>] <message>" - no thread id, no
        // source/caller segment (see the class doc comment for why those don't port here).
        assertEquals(
            "2026-09-06T14:02:11.123Z [Info   ] hello",
            ReqnrollDebugLogger.formatLine(Instant.parse("2026-09-06T14:02:11.123Z"), "Info", "hello"),
        )
    }

    @Test
    fun `formatLine pads every real level to the same width`() {
        assertEquals(
            "2026-09-06T14:02:11.123Z [Error  ] x",
            ReqnrollDebugLogger.formatLine(Instant.parse("2026-09-06T14:02:11.123Z"), "Error", "x"),
        )
        assertEquals(
            "2026-09-06T14:02:11.123Z [Warning] x",
            ReqnrollDebugLogger.formatLine(Instant.parse("2026-09-06T14:02:11.123Z"), "Warning", "x"),
        )
    }

    @Test
    fun `logDirectory uses LOCALAPPDATA on Windows`() {
        assertEquals(
            File("C:\\Users\\me\\AppData\\Local", "Reqnroll"),
            ReqnrollDebugLogger.logDirectory("Windows 11", "C:\\Users\\me\\AppData\\Local", "C:\\Users\\me"),
        )
    }

    @Test
    fun `logDirectory falls back to home when LOCALAPPDATA is unset on Windows`() {
        assertEquals(
            File("C:\\Users\\me", "Reqnroll"),
            ReqnrollDebugLogger.logDirectory("Windows 11", null, "C:\\Users\\me"),
        )
    }

    @Test
    fun `logDirectory uses Library-Logs on macOS`() {
        assertEquals(
            File("/Users/me", "Library/Logs/Reqnroll"),
            ReqnrollDebugLogger.logDirectory("Mac OS X", null, "/Users/me"),
        )
    }

    @Test
    fun `logDirectory falls back to XDG-style local-share for anything else`() {
        assertEquals(
            File("/home/me", ".local/share/Reqnroll"),
            ReqnrollDebugLogger.logDirectory("Linux", null, "/home/me"),
        )
    }

    @Test
    fun `logDirectory os detection is case-insensitive`() {
        assertEquals(
            File("C:\\Users\\me", "Reqnroll"),
            ReqnrollDebugLogger.logDirectory("WINDOWS 10", null, "C:\\Users\\me"),
        )
    }

    // Console-sink dispatch (issue #662) — the "Reqnroll" tool window's own mirroring mechanism.
    // These exercise the real info/warn/error entry points (unlike the pure-function tests above)
    // since the sink dispatch happens inside `log()`; the file write itself stays best-effort/
    // exception-swallowed either way, so this doesn't risk failing the test on a read-only CI
    // filesystem.

    private class RecordingSink : ReqnrollConsoleSink {
        val entries = mutableListOf<Triple<String, String, Throwable?>>()
        override fun accept(level: String, message: String, throwable: Throwable?) {
            entries.add(Triple(level, message, throwable))
        }
    }

    @Test
    fun `curated = true entries are dispatched to every registered console sink`() {
        val sink = RecordingSink()
        ReqnrollDebugLogger.addConsoleSink(sink)
        try {
            ReqnrollDebugLogger.info("hello", curated = true)
        } finally {
            ReqnrollDebugLogger.removeConsoleSink(sink)
        }

        assertEquals(listOf(Triple<String, String, Throwable?>("Info", "hello", null)), sink.entries)
    }

    @Test
    fun `curated = false (the default) never reaches a registered console sink`() {
        val sink = RecordingSink()
        ReqnrollDebugLogger.addConsoleSink(sink)
        try {
            ReqnrollDebugLogger.info("chatter")
            ReqnrollDebugLogger.warn("also chatter")
        } finally {
            ReqnrollDebugLogger.removeConsoleSink(sink)
        }

        assertEquals(emptyList(), sink.entries)
    }

    @Test
    fun `removeConsoleSink stops further dispatch to that sink`() {
        val sink = RecordingSink()
        ReqnrollDebugLogger.addConsoleSink(sink)
        ReqnrollDebugLogger.removeConsoleSink(sink)

        ReqnrollDebugLogger.error("too late", curated = true)

        assertEquals(emptyList(), sink.entries)
    }
}
