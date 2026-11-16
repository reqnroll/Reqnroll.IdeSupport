package net.reqnroll.idesupport.rider.testrunner

import java.nio.file.Files
import kotlin.io.path.createDirectories
import kotlin.io.path.writeText
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class BackendTestGutterMarksTest {
    @Test
    fun `the Run lens leaves scenarios to the gutter marks only when the backend is installed`() {
        assertTrue(BackendTestGutterMarks.hideScenarioRunLens(backendInstalled = true, showRunLensRequested = false))
        assertFalse(BackendTestGutterMarks.hideScenarioRunLens(backendInstalled = false, showRunLensRequested = false))
    }

    @Test
    fun `the registry key brings the Run lens back`() {
        assertFalse(BackendTestGutterMarks.hideScenarioRunLens(backendInstalled = true, showRunLensRequested = true))
    }

    @Test
    fun `the backend is found where prepareSandbox packages it`() {
        val pluginDir = Files.createTempDirectory("reqnroll-plugin")
        try {
            assertFalse(BackendTestGutterMarks.isBackendIn(pluginDir))

            val assembly = pluginDir.resolve(BackendTestGutterMarks.BACKEND_ASSEMBLY)
            assembly.parent.createDirectories()
            assembly.writeText("not really an assembly")

            assertTrue(BackendTestGutterMarks.isBackendIn(pluginDir))
        } finally {
            pluginDir.toFile().deleteRecursively()
        }
    }
}
