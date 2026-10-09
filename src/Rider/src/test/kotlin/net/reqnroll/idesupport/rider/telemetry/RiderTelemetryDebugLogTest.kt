package net.reqnroll.idesupport.rider.telemetry

import com.google.gson.JsonParser
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

// Mirrors Reqnroll.IdeSupport.Common.Tests.Logging.TelemetryDebugLogTests and VS Code's
// telemetryDebugLog.test.ts (issue #799) — same resolution rules and record shape, so VS, VS
// Code, Rider and the LSP server all produce one consistent reqnroll-telemetry-{date}.jsonl.
class RiderTelemetryDebugLogTest {
    @Test
    fun `fromValue returns disabled sink for off values`() {
        for (value in listOf(null, "", "   ", "0", "false", "False")) {
            assertTrue(RiderTelemetryDebugLog.fromValue(value) is NullTelemetryDebugLog, "value=$value")
        }
    }

    @Test
    fun `fromValue returns a file sink for on values`() {
        for (value in listOf("1", "true", "TRUE")) {
            assertTrue(RiderTelemetryDebugLog.fromValue(value) is FileTelemetryDebugLog, "value=$value")
        }
    }

    @Test
    fun `fromValue treats other values as a file path`() {
        assertTrue(RiderTelemetryDebugLog.fromValue("C:\\some\\telemetry.jsonl") is FileTelemetryDebugLog)
    }

    @Test
    fun `FileTelemetryDebugLog appends one json object per event`() {
        val file = File.createTempFile("reqnroll-tel-", ".jsonl")
        file.delete()
        try {
            val sink = FileTelemetryDebugLog(file)

            sink.record(
                "server",
                "Reqnroll Discovery executed",
                mapOf("DiscoverySource" to "Connector", "StepDefinitionCount" to 42),
            )
            sink.record("host", "Extension loaded", null, enabled = true, transmitted = true)

            val lines = file.readLines()
            assertEquals(2, lines.size)

            val first = JsonParser.parseString(lines[0]).asJsonObject
            assertEquals("server", first["source"].asString)
            assertEquals("Reqnroll Discovery executed", first["event"].asString)
            assertEquals("Connector", first["props"].asJsonObject["DiscoverySource"].asString)
            assertEquals(42, first["props"].asJsonObject["StepDefinitionCount"].asInt)

            val second = JsonParser.parseString(lines[1]).asJsonObject
            assertEquals("host", second["source"].asString)
            assertTrue(second["enabled"].asBoolean)
            assertTrue(second["transmitted"].asBoolean)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `FileTelemetryDebugLog never throws for an unwritable path`() {
        val sink = FileTelemetryDebugLog(File("Z:\\does\\not\\exist\\invalid<>path.jsonl"))
        sink.record("server", "E", null)
    }

    @Test
    fun `defaultPath ends with a reqnroll-telemetry file name under the shared log directory`() {
        val path = RiderTelemetryDebugLog.defaultPath()
        assertTrue(Regex("""reqnroll-telemetry-\d{8}\.jsonl""").matches(path.name), path.name)
    }

    @Test
    fun `NullTelemetryDebugLog record is a no-op`() {
        assertFalse(NullTelemetryDebugLog is FileTelemetryDebugLog)
        NullTelemetryDebugLog.record("server", "ignored")
    }
}
