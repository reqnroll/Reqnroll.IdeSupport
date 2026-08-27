package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.util.SystemInfo
import java.io.File

/**
 * Resolves the `dotnet` executable to launch. Rider's process `PATH` isn't guaranteed to include
 * it (issue #452) — most concretely, macOS GUI-launched apps commonly inherit a minimal `PATH`
 * (`/usr/bin:/bin:/usr/sbin:/sbin`) that omits a login-shell-only install. Falls back to
 * `DOTNET_ROOT` and well-known per-OS install locations before giving up and returning the bare
 * command name, which then fails with the OS's normal "not found" error.
 */
internal object DotnetCliLocator {
    fun resolve(): String = resolve(System.getenv("PATH"), System.getenv("DOTNET_ROOT"))

    /** `pathEnv`/`dotnetRoot` are threaded in (rather than read via `System.getenv` directly) so this stays testable without mutating real process environment variables. */
    internal fun resolve(pathEnv: String?, dotnetRoot: String?): String {
        if (isOnPath(pathEnv)) return "dotnet"

        dotnetRoot?.let { root ->
            val candidate = File(root, executableName())
            if (candidate.isFile) return candidate.absolutePath
        }

        wellKnownInstallPaths().firstOrNull { File(it).isFile }?.let { return it }

        return "dotnet"
    }

    private fun isOnPath(pathEnv: String?): Boolean {
        if (pathEnv == null) return false
        val name = executableName()
        return pathEnv.split(File.pathSeparatorChar).any { File(it, name).isFile }
    }

    private fun executableName(): String = if (SystemInfo.isWindows) "dotnet.exe" else "dotnet"

    private fun wellKnownInstallPaths(): List<String> = when {
        SystemInfo.isWindows -> listOfNotNull(
            System.getenv("ProgramFiles")?.let { "$it\\dotnet\\dotnet.exe" },
            System.getenv("ProgramFiles(x86)")?.let { "$it\\dotnet\\dotnet.exe" },
        )
        SystemInfo.isMac -> listOf(
            "/usr/local/share/dotnet/dotnet",
            "/opt/homebrew/share/dotnet/dotnet",
        )
        else -> listOf(
            "/usr/share/dotnet/dotnet",
            "/usr/lib/dotnet/dotnet",
        )
    }
}
