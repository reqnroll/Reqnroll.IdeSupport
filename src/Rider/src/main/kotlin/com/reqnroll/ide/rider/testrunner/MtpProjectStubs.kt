package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * Connects a project to the Microsoft.Testing.Platform (MTP) reporter by writing a project-local
 * stub, `obj/<Project>.csproj.reqnroll-ide.targets`, that imports the bundled
 * `Reqnroll.IdeSupport.TestReporter.MTP.targets` (issue #741) — the Rider counterpart of the Visual
 * Studio extension's `MtpProjectStubs.cs` and VS Code's `mtpProjectStubs.ts`.
 *
 * `Microsoft.Common.targets` imports `$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets`,
 * and `MSBuildProjectExtensionsPath` defaults to `obj/` — the mechanism NuGet's own
 * `obj/<Project>.csproj.nuget.g.targets` uses — so the stub affects that one project only. It replaces
 * the per-run `CustomAfterMicrosoftCommonTargets` environment variable, which reached only builds
 * this plugin started itself. All gating (language, test host, MTP opt-in, TFM, LangVersion, resolved
 * Microsoft.Testing.Platform version) lives in the imported `.targets` file, evaluated by MSBuild at
 * build time, so this object decides only *where* the stub goes.
 */
internal object MtpProjectStubs {
    const val STUB_FILE_SUFFIX = ".reqnroll-ide.targets"

    /** A property that moves obj/ (or the project-extensions path) away from its default; its presence means "ask MSBuild" instead of assuming `obj/`. */
    private val EXTENSIONS_PATH_OVERRIDE =
        Regex("""<\s*(BaseIntermediateOutputPath|MSBuildProjectExtensionsPath|UseArtifactsOutput|ArtifactsPath)\b""", RegexOption.IGNORE_CASE)

    private const val MSBUILD_EVAL_TIMEOUT_SECONDS = 30L

    private val evaluatedDirectories = ConcurrentHashMap<String, String>()

    /**
     * The stub: the bundle path in a property, imported through that property. The path is MSBuild-escaped
     * (`%` `$` `@` `;`) and XML-escaped, and never appears inside a quoted condition literal: the
     * plugin's install path contains the user name, and a name such as O'Brien, or a path with `&` or
     * `$`, must not turn the stub into a file that breaks every build of the project.
     */
    fun buildStubXml(bundleTargetsPath: String): String =
        "<Project>\n" +
            "  <!-- Written by the Reqnroll IDE extension (issue #741): connects this project to the Reqnroll\n" +
            "       Microsoft.Testing.Platform test-outcome reporter. Project-local and inert when the extension\n" +
            "       is not installed. Opt out with <ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>. -->\n" +
            "  <PropertyGroup>\n" +
            "    <_ReqnrollIdeMtpReporterBundle>${escapeForMsbuildXml(bundleTargetsPath)}</_ReqnrollIdeMtpReporterBundle>\n" +
            "  </PropertyGroup>\n" +
            "  <Import Project=\"\$(_ReqnrollIdeMtpReporterBundle)\" Condition=\"Exists('\$(_ReqnrollIdeMtpReporterBundle)')\" />\n" +
            "</Project>\n"

    /** MSBuild escaping (`%` first) then XML text escaping. */
    internal fun escapeForMsbuildXml(value: String): String =
        value.replace("%", "%25").replace("$", "%24").replace("@", "%40").replace(";", "%3B")
            .replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    /**
     * The directory MSBuild imports `$(MSBuildProjectFile).*.targets` from. Fast path: `<project dir>/obj`,
     * unless the project file or a `Directory.Build.props` above it mentions a property that moves it —
     * then [evaluate] (a real MSBuild evaluation) decides, and null means "unknown, don't write".
     */
    fun resolveProjectExtensionsDirectory(
        projectFile: String,
        readTextOrNull: (File) -> String? = ::readTextOrNullFs,
        evaluate: (String) -> String? = ::evaluateViaMsbuildCached,
    ): File? {
        val project = File(projectFile)
        val projectDirectory = project.absoluteFile.parentFile ?: return null

        val moved = readTextOrNull(project)?.let(EXTENSIONS_PATH_OVERRIDE::containsMatchIn) == true ||
            generateSequence(projectDirectory) { it.parentFile }
                .any { dir -> readTextOrNull(File(dir, "Directory.Build.props"))?.let(EXTENSIONS_PATH_OVERRIDE::containsMatchIn) == true }
        if (!moved) return File(projectDirectory, "obj")

        val evaluated = evaluate(projectFile)?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        val path = File(evaluated)
        return if (path.isAbsolute) path else File(projectDirectory, evaluated).normalize()
    }

    /**
     * Writes (or refreshes) the stub for [projectFile]; leaves an up-to-date stub untouched so its
     * timestamp doesn't disturb incremental builds. Returns the stub, or null when skipped/failed —
     * never throws: a missing stub only means no MTP outcomes, never a failed run.
     */
    fun writeStub(
        projectFile: String,
        bundleTargetsPath: String,
        readTextOrNull: (File) -> String? = ::readTextOrNullFs,
        evaluate: (String) -> String? = ::evaluateViaMsbuildCached,
    ): File? {
        return try {
            if (!projectFile.endsWith(".csproj", ignoreCase = true)) return null
            val directory = resolveProjectExtensionsDirectory(projectFile, readTextOrNull, evaluate) ?: run {
                ReqnrollDebugLogger.warn("MtpProjectStubs: could not determine MSBuildProjectExtensionsPath for $projectFile; no MTP reporter stub written")
                return null
            }
            val stub = File(directory, File(projectFile).name + STUB_FILE_SUFFIX)
            val xml = buildStubXml(bundleTargetsPath)
            if (stub.isFile && stub.readText() == xml) return stub
            directory.mkdirs()
            stub.writeText(xml)
            stub
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("MtpProjectStubs: could not write the MTP reporter stub for $projectFile", ex)
            null
        }
    }

    private fun evaluateViaMsbuildCached(projectFile: String): String? =
        evaluatedDirectories[projectFile] ?: evaluateViaMsbuild(projectFile)?.also { evaluatedDirectories[projectFile] = it }

    /** `dotnet msbuild <project> -getProperty:MSBuildProjectExtensionsPath` (a single property prints the bare value). Null on any failure or timeout. */
    internal fun evaluateViaMsbuild(projectFile: String): String? =
        try {
            val process = ProcessBuilder(
                DotnetCliLocator.resolve(), "msbuild", projectFile, "-getProperty:MSBuildProjectExtensionsPath", "-nologo",
            )
                .redirectError(ProcessBuilder.Redirect.DISCARD)
                .start()
            val output = process.inputStream.bufferedReader().use { it.readText() }
            if (!process.waitFor(MSBUILD_EVAL_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
                process.destroyForcibly()
                null
            } else if (process.exitValue() != 0) {
                null
            } else {
                output.trim().takeIf { it.isNotEmpty() }
            }
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("MtpProjectStubs: MSBuildProjectExtensionsPath evaluation failed for $projectFile", ex)
            null
        }

    private fun readTextOrNullFs(file: File): String? =
        try {
            if (file.isFile) file.readText() else null
        } catch (ex: java.io.IOException) {
            null
        }
}
