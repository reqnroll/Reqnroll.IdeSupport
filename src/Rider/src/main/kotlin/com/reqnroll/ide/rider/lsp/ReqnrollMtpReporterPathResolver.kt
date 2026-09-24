package com.reqnroll.ide.rider.lsp

import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import java.nio.file.Files
import java.nio.file.Path

/**
 * Resolves the bundled Microsoft.Testing.Platform (MTP) reporter *source bundle* (issue #741) — the
 * MTP counterpart to [ReqnrollTestLoggerPathResolver], packaged under `mtpreporter/`:
 * `Reqnroll.IdeSupport.TestReporter.MTP.targets` plus the `ReporterSource/` C# files it compiles into
 * a user's test project.
 *
 * Callers need the `.targets` file's own path: `RunTestRunner` writes it into the project's
 * `obj/<Project>.csproj.reqnroll-ide.targets` stub as an `Import` (see `MtpProjectStubs`).
 */
object ReqnrollMtpReporterPathResolver {
    private val PLUGIN_ID = PluginId.getId("com.reqnroll.idesupport")

    const val BUNDLE_TARGETS_FILE_NAME = "Reqnroll.IdeSupport.TestReporter.MTP.targets"
    const val SOURCE_DIRECTORY_NAME = "ReporterSource"

    /** Returns null (not thrown) if the plugin or a complete bundle can't be found — degrades to exit-code-only outcomes for MTP-mode projects, never a hard failure. */
    fun resolve(): Path? {
        val plugin = PluginManagerCore.getPlugin(PLUGIN_ID) ?: return null
        return resolveIn(plugin.pluginPath.resolve("mtpreporter"))
    }

    /** The bundle's `.targets` file in [bundleDirectory], or null unless both it and its `ReporterSource/` directory exist. */
    internal fun resolveIn(bundleDirectory: Path): Path? {
        val targets = bundleDirectory.resolve(BUNDLE_TARGETS_FILE_NAME)
        return targets.takeIf { Files.isRegularFile(it) && Files.isDirectory(bundleDirectory.resolve(SOURCE_DIRECTORY_NAME)) }
    }
}
