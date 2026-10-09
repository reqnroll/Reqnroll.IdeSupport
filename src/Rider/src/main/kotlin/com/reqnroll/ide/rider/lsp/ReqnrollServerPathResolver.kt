package com.reqnroll.ide.rider.lsp

import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import java.io.File
import java.nio.file.Path

/**
 * Resolves the bundled Reqnroll.IdeSupport.LSP.Server executable for the current OS/arch,
 * mirroring the `server/<rid>/<binary>` layout used by the VS Code extension
 * (see src/VSCode/src/extension.ts resolveServerPath).
 */
object ReqnrollServerPathResolver {
    // Marketplace plugin ID (gradle.properties' `pluginId`) — deliberately not
    // "com.reqnroll.ide.rider": the Plugin Verifier rejects IDs containing "rider".
    private val PLUGIN_ID = PluginId.getId("com.reqnroll.idesupport")

    /** Locates the bundled server executable for the current OS/arch under this plugin's own install directory; throws with a descriptive message if it isn't there. */
    fun resolve(): Path {
        val plugin = PluginManagerCore.getPlugin(PLUGIN_ID)
            ?: error("Reqnroll plugin descriptor '$PLUGIN_ID' not found")

        val rid = rid(System.getProperty("os.name"), System.getProperty("os.arch"))
        val binaryName = binaryName(System.getProperty("os.name"))
        val candidate = plugin.pluginPath.resolve("server").resolve(rid).resolve(binaryName)

        if (candidate.toFile().exists()) {
            ensureExecutable(candidate.toFile(), System.getProperty("os.name"))
            return candidate
        }

        error(
            "Reqnroll LSP server not found at $candidate. " +
                "Ensure the server is published and bundled under server/$rid/ for this plugin."
        )
    }

    /**
     * On POSIX systems a bundled server that lost its executable bit (zip round-trips don't
     * preserve it) cannot be spawned; try to restore it, and fail with an actionable message
     * rather than a bare process-start error if that isn't possible. No-op on Windows.
     */
    internal fun ensureExecutable(file: File, osName: String) {
        if (isWindows(osName) || file.canExecute()) return
        if (file.setExecutable(true) && file.canExecute()) return
        error(
            "Reqnroll LSP server at $file is not executable and its permissions could not be changed. " +
                "Run 'chmod +x \"$file\"' (and the same for the bundled reqnroll-ide-connector binaries) and restart the IDE."
        )
    }

    /**
     * Pure functions taking explicit `os.name`/`os.arch` values rather than reading
     * `System.getProperty` directly — lets [resolve]'s RID/binary-name selection be unit tested
     * for every OS/arch combination without mutating global JVM system properties.
     */
    internal fun isWindows(osName: String) = osName.lowercase().contains("win")

    internal fun binaryName(osName: String) =
        if (isWindows(osName)) "Reqnroll.IdeSupport.LSP.Server.exe" else "Reqnroll.IdeSupport.LSP.Server"

    internal fun rid(osName: String, osArch: String): String {
        val os = osName.lowercase()
        val arch = osArch.lowercase()
        return when {
            os.contains("win") -> if (arch.contains("aarch64") || arch.contains("arm64")) "win-arm64" else "win-x64"
            os.contains("mac") -> if (arch.contains("aarch64") || arch.contains("arm")) "osx-arm64" else "osx-x64"
            os.contains("linux") -> if (arch.contains("aarch64") || arch.contains("arm64")) "linux-arm64" else "linux-x64"
            else -> "linux-x64"
        }
    }
}
