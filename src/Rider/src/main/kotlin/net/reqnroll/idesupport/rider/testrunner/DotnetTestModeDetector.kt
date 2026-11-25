package net.reqnroll.idesupport.rider.testrunner

import com.google.gson.JsonParser
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import java.io.File
import java.util.concurrent.TimeUnit

/** Which `dotnet test` command-line shape a project needs. See [DotnetTestModeDetector.detect]. */
internal enum class DotnetTestMode { VS_TEST, MTP_COMPAT, MTP_NATIVE }

/**
 * Works out which `dotnet test` shape a project needs. The VSTest shape (`--logger trx`) and the two
 * Microsoft.Testing.Platform (MTP) shapes are incompatible: native MTP mode rejects `--logger` outright,
 * and an MTP project on the .NET 10 SDK rejects the VSTest target.
 *
 * The primary source is a real MSBuild evaluation ([evaluateMtpPropertiesViaMsbuild]). It is the only
 * way to see a property set by an imported `.props` file: referencing the full `xunit.v3` runner package
 * pulls in `microsoft.testing.platform.msbuild`, which sets `IsTestingPlatformApplication=true` without it
 * ever appearing in the project file's text. When that evaluation cannot run, a text scan of the project
 * file and every `Directory.Build.props` up to the repository root stands in for it.
 *
 * Needs no UI. Everything apart from the MSBuild evaluation and file reads is pure, and tests pass
 * `{ null }` as the evaluator to exercise the text scan without starting `dotnet msbuild`.
 */
internal object DotnetTestModeDetector {
    /** Matches an MSBuild property element that declares a project MTP-*capable*, set to `true`. */
    private val MTP_CAPABLE_PATTERN = Regex(
        """<(EnableMSTestRunner|EnableNUnitRunner|UseMicrosoftTestingPlatformRunner|IsTestingPlatformApplication)>\s*true\s*<""",
        RegexOption.IGNORE_CASE,
    )

    /** Matches the MSBuild property that redirects the *legacy* `dotnet test` CLI to the MTP host for an MTP-capable project, set to `true`. */
    private val DOTNET_TEST_MTP_REDIRECT_PATTERN = Regex(
        """<TestingPlatformDotnetTestSupport>\s*true\s*<""",
        RegexOption.IGNORE_CASE,
    )

    /** Matches `global.json`'s opt-in to the *native* `dotnet test` MTP mode (.NET 10 SDK+). */
    private val GLOBAL_JSON_MTP_RUNNER_PATTERN = Regex(
        """"runner"\s*:\s*"Microsoft\.Testing\.Platform"""",
        RegexOption.IGNORE_CASE,
    )

    /** The properties that each make a project MTP-*capable* when `true` (the same vocabulary as [MTP_CAPABLE_PATTERN]). */
    private val MTP_CAPABLE_PROPERTY_NAMES = listOf(
        "EnableMSTestRunner", "EnableNUnitRunner", "UseMicrosoftTestingPlatformRunner", "IsTestingPlatformApplication",
    )

    /** The property for the legacy `dotnet test` redirect (the same as [DOTNET_TEST_MTP_REDIRECT_PATTERN]). */
    private const val MTP_REDIRECT_PROPERTY_NAME = "TestingPlatformDotnetTestSupport"

    /** How long to wait for `dotnet msbuild -getProperty:...` before giving up and falling back to the text scan. */
    private const val MSBUILD_EVAL_TIMEOUT_SECONDS = 20L

    /** The two MSBuild-evaluated signals. See [evaluateMtpPropertiesViaMsbuild]. */
    internal data class MtpEvaluation(val mtpCapable: Boolean, val dotnetTestRedirectsToMtp: Boolean)

    /** Everything [detect] needs, gathered in one walk up from the project folder. */
    private data class ProjectSignals(
        val mtpCapable: Boolean,
        val dotnetTestRedirectsToMtp: Boolean,
        val nativeModeOptIn: Boolean,
    )

    /**
     * The `dotnet test` mode for [projectFilePath]. Two independent signals decide it:
     * - **Capability** (per project): one of [MTP_CAPABLE_PROPERTY_NAMES] is `true`.
     * - **Which MTP shape**, if capable: native mode when a `global.json` up to the repository root opts
     *   in (overridden by the `DOTNET_TEST_RUNNER` environment variable), else compat mode when the
     *   project sets [MTP_REDIRECT_PROPERTY_NAME].
     *
     * Capability alone is not enough. A project can set `EnableMSTestRunner` or `EnableNUnitRunner` and
     * still run under plain VSTest, writing a TRX as usual, when neither shape is switched on.
     */
    internal fun detect(
        projectFilePath: String,
        evaluateMtp: (String) -> MtpEvaluation? = ::evaluateMtpPropertiesViaMsbuild,
    ): DotnetTestMode {
        val signals = collectSignals(File(projectFilePath), evaluateMtp(projectFilePath))

        // DOTNET_TEST_RUNNER (.NET 11 Preview 6+) overrides global.json for the current process when
        // recognized. Not live-verified, as the repository's SDK predates it; an unset or unrecognized
        // value leaves the global.json signal unchanged.
        val nativeModeActive = when (System.getenv("DOTNET_TEST_RUNNER")?.trim()?.lowercase()) {
            "microsoft.testing.platform" -> true
            "vstest" -> false
            else -> signals.nativeModeOptIn
        }

        return when {
            !signals.mtpCapable -> DotnetTestMode.VS_TEST
            nativeModeActive -> DotnetTestMode.MTP_NATIVE
            signals.dotnetTestRedirectsToMtp -> DotnetTestMode.MTP_COMPAT
            else -> DotnetTestMode.VS_TEST
        }
    }

    /**
     * Whether [projectFilePath] is MTP-capable at all, regardless of which shape [detect] picks.
     *
     * This answers a different question from `detect(...) != VS_TEST`, and the VSTest run uses it to
     * tell an MTP quirk from a real launch failure when no TRX appears. A project with
     * `IsTestingPlatformApplication=true` but neither [MTP_REDIRECT_PROPERTY_NAME] nor a native
     * `global.json` opt-in correctly resolves to VS_TEST, since that is the right command line to try.
     * Yet on the .NET 10 SDK, `Microsoft.Testing.Platform.MSBuild.targets` rejects every `dotnet test`
     * of such a project ("Testing with VSTest target is no longer supported..."), with or without
     * `--logger`.
     */
    internal fun isMtpCapable(
        projectFilePath: String,
        evaluateMtp: (String) -> MtpEvaluation? = ::evaluateMtpPropertiesViaMsbuild,
    ): Boolean = collectSignals(File(projectFilePath), evaluateMtp(projectFilePath)).mtpCapable

    /**
     * Reads the project file and walks up its folders to the repository root (the first folder with a
     * `.git` entry). `global.json` is always read. The text scan of the project file and of
     * `Directory.Build.props` only applies when [evaluation] is null: a real evaluation already reflects
     * every imported file's resolved value, conditions included, and a text match inside a
     * condition-guarded `PropertyGroup` must not override its `false`.
     */
    private fun collectSignals(projectFile: File, evaluation: MtpEvaluation?): ProjectSignals {
        val projectText = if (evaluation == null) readTextOrNull(projectFile) else null
        var mtpCapable = evaluation?.mtpCapable ?: (projectText?.let(MTP_CAPABLE_PATTERN::containsMatchIn) == true)
        var redirectsToMtp = evaluation?.dotnetTestRedirectsToMtp
            ?: (projectText?.let(DOTNET_TEST_MTP_REDIRECT_PATTERN::containsMatchIn) == true)
        var nativeModeOptIn = false

        var dir = projectFile.parentFile
        while (dir != null) {
            if (evaluation == null) {
                readTextOrNull(File(dir, "Directory.Build.props"))?.let { props ->
                    if (MTP_CAPABLE_PATTERN.containsMatchIn(props)) mtpCapable = true
                    if (DOTNET_TEST_MTP_REDIRECT_PATTERN.containsMatchIn(props)) redirectsToMtp = true
                }
            }
            readTextOrNull(File(dir, "global.json"))?.let { json ->
                if (GLOBAL_JSON_MTP_RUNNER_PATTERN.containsMatchIn(json)) nativeModeOptIn = true
            }
            if (File(dir, ".git").exists()) break
            dir = dir.parentFile
        }
        return ProjectSignals(mtpCapable, redirectsToMtp, nativeModeOptIn)
    }

    /**
     * Evaluates [projectFilePath]'s MSBuild-resolved MTP properties with `dotnet msbuild -getProperty:...`,
     * the same approach as VS Code's `msbuildEvaluator.ts`. Rider's `RunnableProject` model offers no
     * generic property read, so this shells out.
     *
     * Returns null, and callers fall back to the text scan, if the process cannot start, times out,
     * exits non-zero, or prints something other than `{"Properties": {...}}`. It never throws, so a
     * broken or unrestorable project cannot block a Run click.
     */
    internal fun evaluateMtpPropertiesViaMsbuild(projectFilePath: String): MtpEvaluation? {
        val propertyNames = MTP_CAPABLE_PROPERTY_NAMES + MTP_REDIRECT_PROPERTY_NAME
        return try {
            val result = BoundedProcessRunner.run(
                listOf(
                    DotnetCliLocator.resolve(), "msbuild", projectFilePath,
                    "-getProperty:${propertyNames.joinToString(",")}", "-nologo",
                ),
                TimeUnit.SECONDS.toMillis(MSBUILD_EVAL_TIMEOUT_SECONDS),
            ) ?: return null
            if (result.exitCode != 0) return null

            val properties = JsonParser.parseString(result.output).asJsonObject.getAsJsonObject("Properties")
                ?: return null
            fun isTrue(name: String) = properties.get(name)
                ?.takeIf { it.isJsonPrimitive }
                ?.asString
                ?.trim()
                ?.equals("true", ignoreCase = true) == true

            MtpEvaluation(
                mtpCapable = MTP_CAPABLE_PROPERTY_NAMES.any(::isTrue),
                dotnetTestRedirectsToMtp = isTrue(MTP_REDIRECT_PROPERTY_NAME),
            )
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn(
                "DotnetTestModeDetector: MSBuild property evaluation failed for $projectFilePath; falling back to a text scan", ex)
            null
        }
    }

    private fun readTextOrNull(file: File): String? =
        try {
            if (file.isFile) file.readText() else null
        } catch (ex: java.io.IOException) {
            null // Unreadable (permissions, mid-write): treat as "no evidence", not a crash.
        }
}
