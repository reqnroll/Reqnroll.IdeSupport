package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.util.SystemInfo
import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals

class DotnetCliLocatorTest {
    private var tmpDir: File? = null

    @AfterTest
    fun cleanup() {
        tmpDir?.deleteRecursively()
    }

    private fun executableName() = if (SystemInfo.isWindows) "dotnet.exe" else "dotnet"

    @Test
    fun `returns the bare command name when dotnet is found on PATH`() {
        val dir = Files.createTempDirectory("reqnroll-dotnet-locator-").toFile().also { tmpDir = it }
        File(dir, executableName()).createNewFile()

        assertEquals("dotnet", DotnetCliLocator.resolve(pathEnv = dir.absolutePath, dotnetRoot = null))
    }

    @Test
    fun `falls back to DOTNET_ROOT when dotnet is not on PATH`() {
        val dir = Files.createTempDirectory("reqnroll-dotnet-locator-").toFile().also { tmpDir = it }
        val executable = File(dir, executableName()).also { it.createNewFile() }

        assertEquals(executable.absolutePath, DotnetCliLocator.resolve(pathEnv = "", dotnetRoot = dir.absolutePath))
    }

    @Test
    fun `ignores a DOTNET_ROOT that does not contain the executable`() {
        val dir = Files.createTempDirectory("reqnroll-dotnet-locator-").toFile().also { tmpDir = it }

        // No well-known install path exists in this empty temp dir hierarchy, but the real,
        // machine-specific well-known paths (e.g. /usr/share/dotnet) aren't stubbable here — so
        // this only asserts the DOTNET_ROOT candidate itself is rejected, not the final fallback.
        val result = DotnetCliLocator.resolve(pathEnv = "", dotnetRoot = dir.absolutePath)
        assert(result != File(dir, executableName()).absolutePath) {
            "expected the empty DOTNET_ROOT candidate to be rejected, got $result"
        }
    }
}
