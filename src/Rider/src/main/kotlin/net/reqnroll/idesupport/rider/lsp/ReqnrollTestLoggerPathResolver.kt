package net.reqnroll.idesupport.rider.lsp

import net.reqnroll.idesupport.rider.ReqnrollPlugin
import java.nio.file.Path

/**
 * Resolves the directory containing the bundled `Reqnroll.IdeSupport.TestLogger.dll` (LSP-server
 * outcome pipeline) — the same VSTest logger the Visual Studio extension bundles under
 * its own `TestLogger/` subdirectory, packaged here under `testlogger/` instead.
 *
 * Unlike [ReqnrollServerPathResolver], there is no per-OS/arch RID to resolve: the logger targets
 * netstandard2.0 with no self-contained runtime (see its own project file's remarks) and loads
 * inside whichever `dotnet test`/vstest process is already running, on any OS — one build serves
 * every platform.
 */
object ReqnrollTestLoggerPathResolver {

    const val ASSEMBLY_FILE_NAME = "Reqnroll.IdeSupport.TestLogger.dll"

    /** Locates the bundled logger's directory under this plugin's own install directory; returns null (not thrown — a missing logger degrades to the TRX-only path, never a hard failure) if it isn't there. */
    fun resolve(): Path? {
        val plugin = ReqnrollPlugin.descriptor() ?: return null
        val candidate = plugin.pluginPath.resolve("testlogger")
        return candidate.takeIf { it.resolve(ASSEMBLY_FILE_NAME).toFile().exists() }
    }
}
