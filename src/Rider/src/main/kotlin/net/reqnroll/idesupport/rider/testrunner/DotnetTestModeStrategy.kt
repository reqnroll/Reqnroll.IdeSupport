package net.reqnroll.idesupport.rider.testrunner

import net.reqnroll.idesupport.rider.lsp.ReqnrollMtpReporterPathResolver
import net.reqnroll.idesupport.rider.lsp.ReqnrollTestLoggerPathResolver
import net.reqnroll.idesupport.rider.lsp.protocol.RegisterTestRunResponse
import java.io.File
import java.nio.file.Files
import java.nio.file.Path

/** The outcome of one `dotnet test` run. [Failure.message] is shown to the user verbatim. */
internal sealed class DotnetTestOutcome {
    /** The run finished and produced [result] (from the TRX or the exit code); a test failure is still a completed run. */
    data class Completed(val result: RunResult) : DotnetTestOutcome()

    data class Failure(val message: String) : DotnetTestOutcome()

    /** The user cancelled the run; the process tree has been killed. Deliberately silent at the call site. */
    object Cancelled : DotnetTestOutcome()

    /**
     * No TRX was produced, but the project is MTP-capable ([DotnetTestModeDetector.isMtpCapable]), so this
     * is an MTP quirk rather than a launch failure. Kept apart from [Failure] so the call site logs it
     * instead of showing a modal error on every Run click for that project.
     */
    data class Inconclusive(val message: String) : DotnetTestOutcome()
}

/**
 * How one [DotnetTestMode] shapes a `dotnet test` run: the extra command-line arguments, any preparation
 * before launch, how a finished process becomes a [DotnetTestOutcome], and whether the LSP server will
 * have per-method outcomes for the run. [RunTestRunner] picks one strategy per run and never checks the
 * mode itself.
 */
internal sealed class DotnetTestModeStrategy {
    /** Whether the bundled logger or reporter will report this run to the LSP server, so polling it is worthwhile. */
    abstract val reportsToServer: Boolean

    /** Arguments appended after `dotnet test <project> --filter <filter>`. */
    abstract fun arguments(): List<String>

    /** Called once before the process starts. */
    open fun prepare(projectFile: String) {}

    /** Turns the finished process's [exitCode] into the run's outcome. */
    abstract fun interpret(projectFile: String, exitCode: Int): DotnetTestOutcome

    /** Called once after the run, whatever its outcome. */
    open fun cleanUp() {}

    companion object {
        /**
         * The strategy for [mode]. [registration] is the LSP server's test-run registration, or null when
         * there is none (no server, or its listener could not start); the run then goes ahead without the
         * logger or reporter and its result comes from the TRX or the exit code alone.
         */
        fun create(mode: DotnetTestMode, registration: RegisterTestRunResponse?): DotnetTestModeStrategy =
            when (mode) {
                DotnetTestMode.VS_TEST ->
                    VsTestStrategy(registration, registration?.let { ReqnrollTestLoggerPathResolver.resolve() })
                DotnetTestMode.MTP_COMPAT, DotnetTestMode.MTP_NATIVE ->
                    MtpStrategy(registration?.let { ReqnrollMtpReporterPathResolver.resolve() })
            }
    }
}

/**
 * VSTest mode: a TRX logger, plus the bundled `Reqnroll.IdeSupport.TestLogger` pointed at the LSP server
 * when [registration] succeeded and [loggerDirectory] was found. Both loggers report from the same run,
 * so the TRX is always there to fall back on.
 */
internal class VsTestStrategy(
    private val registration: RegisterTestRunResponse?,
    private val loggerDirectory: Path?,
    private val isMtpCapable: (String) -> Boolean = { DotnetTestModeDetector.isMtpCapable(it) },
) : DotnetTestModeStrategy() {
    private val resultsDirectory = lazy { Files.createTempDirectory("reqnroll-test-").toFile() }
    private val trxFile get() = File(resultsDirectory.value, TRX_FILE_NAME)

    override val reportsToServer: Boolean get() = registration != null && loggerDirectory != null

    override fun arguments(): List<String> {
        // Pitfall: --nologo is VSTest-only. Under either MTP mode it is not ignored as an unknown flag:
        // its presence alone makes the run report "Zero tests ran" and exit with code 5.
        val args = mutableListOf(
            "--nologo",
            "--logger", "trx;LogFileName=$TRX_FILE_NAME",
            "--results-directory", resultsDirectory.value.absolutePath,
        )
        if (registration != null && loggerDirectory != null) {
            args += listOf(
                "--test-adapter-path", loggerDirectory.toString(),
                "--logger", buildLoggerArgument(registration, ProcessHandle.current().pid()),
            )
        }
        return args
    }

    /**
     * Failing tests give a non-zero exit code, which is not a run failure; only a missing TRX means the
     * run never completed. The exception is an MTP-capable project that [DotnetTestModeDetector.detect]
     * still sent down the VSTest path: there, no TRX may just mean the SDK refused the VSTest target.
     */
    override fun interpret(projectFile: String, exitCode: Int): DotnetTestOutcome {
        val trx = trxFile
        if (trx.exists()) {
            return DotnetTestOutcome.Completed(RunOutcomeCollector.trxResult(TrxParser.parse(trx.readText())))
        }
        return if (isMtpCapable(projectFile)) {
            DotnetTestOutcome.Inconclusive(
                "dotnet test produced no TRX file for $projectFile. The project is Microsoft.Testing.Platform " +
                    "(MTP) capable, but neither TestingPlatformDotnetTestSupport nor a global.json MTP runner " +
                    "was found, so it was run with the VSTest command line."
            )
        } else {
            DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
        }
    }

    override fun cleanUp() {
        if (resultsDirectory.isInitialized()) resultsDirectory.value.deleteRecursively()
    }

    companion object {
        /**
         * The logger's friendly name, the same as `ReqnrollIdeTestLogger` and `TestLoggerRunSettings.cs`.
         * Kept in sync by hand: referencing the logger assembly would put a second copy in the output.
         */
        internal const val LOGGER_FRIENDLY_NAME = "ReqnrollIde"

        private const val TRX_FILE_NAME = "result.trx"

        /**
         * The vstest `--logger` value for the bundled logger, with the same parameter keys as
         * `TestLoggerRunSettings` on the Visual Studio side. None of the values (an endpoint, a hex run
         * id, a numeric pid) needs escaping in vstest's logger syntax. There is no per-connection secret;
         * `TestOutcomeTcpListener`'s remarks on the server side explain why.
         */
        internal fun buildLoggerArgument(registration: RegisterTestRunResponse, ideProcessId: Long): String =
            "$LOGGER_FRIENDLY_NAME;Endpoint=${registration.endpoint};" +
                "RunId=${registration.runId};IdeProcessId=$ideProcessId"
    }
}

/**
 * MTP compat and native modes. No extra arguments: compat mode silently ignores `--logger` and
 * `--test-adapter-path`, and native mode rejects `--logger` and fails the run with exit code 5. `--filter`
 * works unchanged, because MTP accepts the VSTest filter syntax.
 *
 * Outcomes reach the server through the reporter instead. When [reporterBundle] is known, [prepare]
 * writes the project's `obj/<Project>.csproj.reqnroll-ide.targets` stub ([MtpProjectStubs]), which
 * compiles the reporter into the test assembly. It needs no project-file change or environment variable,
 * and it also serves later builds, including ones Rider's own test runner starts.
 */
internal class MtpStrategy(private val reporterBundle: Path?) : DotnetTestModeStrategy() {
    override val reportsToServer: Boolean get() = reporterBundle != null

    override fun arguments(): List<String> = emptyList()

    override fun prepare(projectFile: String) {
        reporterBundle?.let { MtpProjectStubs.writeStub(projectFile, it.toString()) }
    }

    /** MTP writes no TRX, so the exit code is the result. Per-row detail, when available, comes from the server. */
    override fun interpret(projectFile: String, exitCode: Int): DotnetTestOutcome =
        DotnetTestOutcome.Completed(RunOutcomeCollector.exitCodeResult(exitCode))
}
