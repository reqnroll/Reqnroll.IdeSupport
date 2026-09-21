package com.reqnroll.ide.rider.lsp

import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import java.nio.file.Path

/**
 * Resolves the bundled `Reqnroll.IdeSupport.TestReporter.MTP.dll` (issue #715 phase 4) — the
 * Microsoft.Testing.Platform (MTP) counterpart to [ReqnrollTestLoggerPathResolver], packaged under
 * `mtpreporter/` instead of `testlogger/`.
 *
 * Unlike the VSTest logger (injected via runsettings/`--test-adapter-path`, which needs a
 * *directory*), this assembly is referenced via a `HintPath` inside an ephemerally-injected
 * `.targets` file (`RunTestRunner`'s `CustomAfterMicrosoftCommonTargets` mechanism — plan §5.6), so
 * callers need the DLL's own file path.
 */
object ReqnrollMtpReporterPathResolver {
    private val PLUGIN_ID = PluginId.getId("com.reqnroll.idesupport")

    const val ASSEMBLY_FILE_NAME = "Reqnroll.IdeSupport.TestReporter.MTP.dll"

    /** Returns null (not thrown) if the plugin or the bundled DLL can't be found — degrades to exit-code-only outcomes for MTP-mode projects, never a hard failure. */
    fun resolve(): Path? {
        val plugin = PluginManagerCore.getPlugin(PLUGIN_ID) ?: return null
        val candidate = plugin.pluginPath.resolve("mtpreporter").resolve(ASSEMBLY_FILE_NAME)
        return candidate.takeIf { it.toFile().exists() }
    }
}
