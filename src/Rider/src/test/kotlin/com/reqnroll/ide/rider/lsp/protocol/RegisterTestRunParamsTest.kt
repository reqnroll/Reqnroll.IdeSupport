package com.reqnroll.ide.rider.lsp.protocol

import com.google.gson.Gson
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * Pins the wire shape of `reqnroll/testOutcomes/registerRun` params (issue #850): the server counts a Run/Debug
 * request from `runMode`, and ignores a registration without one, so the property name and the values must match
 * the server's `RegisterTestRunParams` / `TestRunModes` exactly.
 */
class RegisterTestRunParamsTest {
    private val gson = Gson()

    @Test
    fun `a run request serialises runMode as the server expects`() {
        assertEquals("""{"runMode":"Run"}""", gson.toJson(RegisterTestRunParams(TestRunMode.RUN)))
    }

    @Test
    fun `a registration without a run mode omits the property so the server does not count it`() {
        assertEquals("{}", gson.toJson(RegisterTestRunParams()))
    }

    @Test
    fun `mode constants match the server wire values`() {
        assertEquals("Run", TestRunMode.RUN)
        assertEquals("Debug", TestRunMode.DEBUG)
        assertEquals("Unknown", TestRunMode.UNKNOWN)
    }
}
