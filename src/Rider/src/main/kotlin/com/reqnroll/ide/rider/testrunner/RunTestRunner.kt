package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.jetbrains.rider.model.RunnableProject
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import com.reqnroll.ide.rider.actions.ReqnrollNotify
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.ReqnrollTestLoggerPathResolver
import com.reqnroll.ide.rider.lsp.lspUriToLocalPath
import com.reqnroll.ide.rider.lsp.protocol.GetTestOutcomeResponse
import com.reqnroll.ide.rider.lsp.protocol.RegisterTestRunResponse
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.TimeUnit

/**
 * Implements the "▶ Run" lens click (design doc §5/§6, issue #262) — Rider's own execution of
 * `dotnet test --filter`, mirroring VS Code's already-shipped "Option 2" for this same issue
 * (this plugin has no native `com.intellij.execution`/Test Runner integration to delegate to
 * instead — see [RunTestCodeVisionProvider]'s doc comment).
 *
 * **LSP-server outcome pipeline (#700/#702).** As of the refactor that moved the VSTest-logger
 * receiver/store/persistence into the shared LSP server, this also registers the run with the
 * server ([ReqnrollRequestSender.registerTestRun]) and — when that succeeds — points the bundled
 * `Reqnroll.IdeSupport.TestLogger` at it via the same two `dotnet test` arguments the Visual
 * Studio extension injects into runsettings (`TestLoggerRunSettings.cs`), then queries
 * [ReqnrollRequestSender.getTestOutcome] per target method afterward for per-row detail (Scenario
 * Outline rows, failed-step text) that plain TRX scraping never captured. TRX parsing itself is
 * untouched and always still runs — both loggers report from the same `dotnet test` invocation —
 * so a server lookup that comes back empty (server not running, logger not bundled correctly, the
 * runner process exited before the server finished processing) falls back to the pre-#700
 * TRX-only result exactly as before. This fallback is deliberately kept for at least one release
 * (implementation plan Phase 4) rather than becoming the only path immediately.
 */
object RunTestRunner {
    private const val TEST_TIMEOUT_SECONDS = 120L

    /** Mirrors ReqnrollIdeTestLogger's/TestLoggerRunSettings.cs's constants — the VS extension deliberately doesn't reference that assembly to avoid a second copy landing in its own output; same reasoning applies here. */
    internal const val LOGGER_FRIENDLY_NAME = "ReqnrollIde"

    /** How long, and how often, to poll the server for outcomes after `dotnet test` exits before giving up and falling back to TRX — the logger's final `runComplete` write and the server's processing of it are not guaranteed to have landed the instant the runner process itself terminates. */
    private const val OUTCOME_POLL_ATTEMPTS = 10
    private const val OUTCOME_POLL_DELAY_MS = 100L

    /** Runs the resolved [targets] on a background task and updates [RunTestResultStore]/the lens once it completes. */
    fun run(project: Project, uri: String, startLine: Int, targets: List<ScenarioTestTargetItem>) {
        ReqnrollDebugLogger.info(
            "RunTestRunner: invoked for $uri:$startLine (${targets.size} target(s))")

        ProgressManager.getInstance().run(object : Task.Backgroundable(project, "Reqnroll: Running Test", true) {
            override fun run(indicator: ProgressIndicator) {
                val filePath = lspUriToLocalPath(uri)
                if (filePath == null) {
                    notifyError(project, "Could not resolve a local path for $uri.")
                    return
                }

                val runnableProjects = project.solution.runnableProjectsModel.projects.valueOrNull.orEmpty()
                val ownerPath = findOwningProjectPath(filePath, runnableProjects.map { it.projectFilePath })
                val runnableProject = runnableProjects.firstOrNull { it.projectFilePath == ownerPath }
                if (runnableProject == null) {
                    notifyError(project, "Could not find the project that owns $filePath.")
                    return
                }

                val filter = buildTestFilter(targets)
                if (filter.isEmpty()) {
                    notifyError(project, "No test target(s) resolved for this scenario.")
                    return
                }

                // Registers before running: the endpoint must be baked into the dotnet test
                // command line below. A null/unsuccessful registration (no server running, or it
                // couldn't start its listener) just means the two extra arguments are omitted —
                // the run proceeds exactly as it did before #700, TRX-only.
                val registration = ReqnrollRequestSender.registerTestRun(project)
                    ?.takeIf { it.success }
                val loggerDirectory = registration?.let { ReqnrollTestLoggerPathResolver.resolve() }

                val testOutcome = runDotnetTest(runnableProject.projectFilePath, filter, registration, loggerDirectory)
                val results = when (testOutcome) {
                    is DotnetTestOutcome.Failure -> {
                        notifyError(project, testOutcome.message)
                        return
                    }
                    is DotnetTestOutcome.Success -> testOutcome.results
                }

                val assemblyPath = registration?.let { outputAssemblyPath(runnableProject) }
                val result = if (registration != null && loggerDirectory != null && assemblyPath != null) {
                    pollServerResult(project, assemblyPath, targets) ?: trxResult(results)
                } else {
                    trxResult(results)
                }
                RunTestResultStore.set(uri, startLine, result)

                ApplicationManager.getApplication().invokeLater {
                    if (!project.isDisposed) RunTestCodeVisionProvider.refreshOpenFeatureEditors(project)
                }
            }
        })
    }

    /**
     * The project file path (from [projectFilePaths]) whose directory contains [filePath] (deepest
     * match wins) — mirrors VS Code's `findOwningProjectFile`/VS's DTE-based lookup. Operates on
     * plain path strings rather than [RunnableProject] directly (an RD-generated protocol model
     * type, not practically constructible in a unit test) so the matching logic itself stays
     * `internal`ly testable without a platform fixture.
     */
    internal fun findOwningProjectPath(filePath: String, projectFilePaths: Collection<String>): String? {
        var best: String? = null
        var bestLen = 0
        for (projectFilePath in projectFilePaths) {
            val folder = File(projectFilePath).parent ?: continue
            val prefix = folder.trimEnd(File.separatorChar) + File.separatorChar
            if (filePath.startsWith(prefix, ignoreCase = true) && prefix.length > bestLen) {
                best = projectFilePath
                bestLen = prefix.length
            }
        }
        return best
    }

    /**
     * Builds a `dotnet test --filter` expression covering every distinct generated method among
     * [targets] — row-tests targets sharing one method collapse to a single term (running that
     * method already runs every row); individual-methods targets each get their own term. Mirrors
     * VS Code's `testFilterBuilder.ts`/VS's `TestMethodIdentifier` dedup. `internal` for testability.
     */
    internal fun buildTestFilter(targets: List<ScenarioTestTargetItem>): String =
        targets
            .map { "${it.declaringTypeFullName}.${it.methodName}" }
            .distinct()
            .joinToString("|") { "FullyQualifiedName=$it" }

    /**
     * The compiled test container path for [runnableProject] — what the bundled VSTest logger
     * reports as `TestCase.Source`, needed to look up outcomes server-side (`TestOutcomeKey`
     * requires it; type+method alone isn't guaranteed unique across containers). Rider's RD
     * project model has no dedicated "test output assembly" concept; [ProjectOutput.exePath] is
     * the field the platform's own run infrastructure uses for exactly this — the built artifact
     * path — for any SDK-style project, executable or not. Returns null (skip the server lookup
     * entirely, fall back to TRX) rather than guessing when a project has no recorded output.
     */
    internal fun outputAssemblyPath(runnableProject: RunnableProject): String? =
        runnableProject.projectOutputs.firstOrNull()?.exePath?.takeIf { it.isNotBlank() }

    /**
     * The vstest `--logger` argument value registering the bundled logger, mirroring
     * `TestLoggerRunSettings`'s friendly name and parameter keys on the Visual Studio side (kept
     * in sync manually — see that class's own note on why it isn't referenced directly). `internal`
     * for testability; vstest's logger URI syntax has no need to escape any of these values (an
     * endpoint, a hex run id, a numeric pid). No per-connection secret — see
     * `TestOutcomeTcpListener`'s remarks (server side) for why an earlier per-run token was tried
     * and removed.
     */
    internal fun buildLoggerArgument(registration: RegisterTestRunResponse, ideProcessId: Long): String =
        "$LOGGER_FRIENDLY_NAME;Endpoint=${registration.endpoint};" +
            "RunId=${registration.runId};IdeProcessId=$ideProcessId"

    /**
     * Polls the server for every distinct target method's outcome, retrying briefly
     * ([OUTCOME_POLL_ATTEMPTS] × [OUTCOME_POLL_DELAY_MS]) since the logger's final `runComplete`
     * write and the server processing it are not guaranteed to have landed the instant the
     * `dotnet test` process itself exits. Returns null (fall back to TRX) if nothing is found
     * after every attempt. Runs on the calling (background task) thread — never call from the EDT.
     */
    private fun pollServerResult(project: Project, assemblyPath: String, targets: List<ScenarioTestTargetItem>): RunResult? {
        val distinctMethods = targets.map { it.declaringTypeFullName to it.methodName }.distinct()
        repeat(OUTCOME_POLL_ATTEMPTS) { attempt ->
            val found = distinctMethods.mapNotNull { (type, method) ->
                ReqnrollRequestSender.getTestOutcome(project, assemblyPath, type, method)?.takeIf { it.found }
            }
            if (found.isNotEmpty()) return combineServerOutcomes(found)
            if (attempt < OUTCOME_POLL_ATTEMPTS - 1) Thread.sleep(OUTCOME_POLL_DELAY_MS)
        }
        return null
    }

    /**
     * Combines one [GetTestOutcomeResponse] per distinct target method into a single [RunResult]
     * for the scenario — `Failed` if any method's aggregate failed, matching the server's own
     * `TestOutcomeStore.Aggregate` precedence (`Failed` > `Passed` > everything else). `internal`
     * for testability.
     */
    internal fun combineServerOutcomes(outcomes: List<GetTestOutcomeResponse>): RunResult {
        val outcome = if (outcomes.any { it.aggregate == "Failed" }) RunOutcome.FAILED else RunOutcome.PASSED
        val rows = outcomes.flatMap { response ->
            response.rows.map { row ->
                RunResultRow(
                    displayName = row.displayName,
                    outcome = if (row.outcome == "Failed") RunOutcome.FAILED else RunOutcome.PASSED,
                    failedStepText = row.failedStepText,
                )
            }
        }
        return RunResult(outcome, rows)
    }

    /** The pre-#700 result shape: one aggregate bit over every TRX row, no per-row detail. */
    private fun trxResult(results: List<TrxUnitTestResult>): RunResult =
        RunResult(if (results.any { it.outcome == "Failed" }) RunOutcome.FAILED else RunOutcome.PASSED)

    /** The outcome of a [runDotnetTest] call — [Failure.message] is shown to the user verbatim, so it distinguishes an unresolvable `dotnet` CLI (issue #452) from every other launch failure. */
    private sealed class DotnetTestOutcome {
        data class Success(val results: List<TrxUnitTestResult>) : DotnetTestOutcome()
        data class Failure(val message: String) : DotnetTestOutcome()
    }

    /**
     * Shells to `dotnet test --filter` with a TRX logger — plus, when [registration] succeeded and
     * [loggerDirectory] is non-null, the bundled `Reqnroll.IdeSupport.TestLogger` alongside it —
     * and parses the TRX result. Returns [DotnetTestOutcome.Failure] when the run itself couldn't
     * be started/completed — a non-zero exit code from failing tests is not itself a failure, only
     * the absence of a TRX file is.
     */
    private fun runDotnetTest(
        projectFile: String,
        filter: String,
        registration: RegisterTestRunResponse?,
        loggerDirectory: Path?,
    ): DotnetTestOutcome {
        val resultsDir = Files.createTempDirectory("reqnroll-test-").toFile()
        val trxFileName = "result.trx"
        val trxFile = File(resultsDir, trxFileName)

        return try {
            val command = mutableListOf(
                DotnetCliLocator.resolve(), "test", projectFile,
                "--filter", filter,
                "--logger", "trx;LogFileName=$trxFileName",
                "--results-directory", resultsDir.absolutePath,
                "--nologo",
            )
            if (registration != null && loggerDirectory != null) {
                command += listOf(
                    "--test-adapter-path", loggerDirectory.toString(),
                    "--logger", buildLoggerArgument(registration, ProcessHandle.current().pid()),
                )
            }
            // Neither stdout nor stderr is read anywhere (results come from the TRX file, not
            // live process output) — Redirect.DISCARD avoids the classic ProcessBuilder deadlock
            // where an un-drained pipe fills its OS buffer and the child blocks writing to it,
            // making `waitFor` hang until the timeout even for a run that would otherwise succeed.
            val process = try {
                ProcessBuilder(command)
                    .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                    .redirectError(ProcessBuilder.Redirect.DISCARD)
                    .start()
            } catch (ex: java.io.IOException) {
                ReqnrollDebugLogger.warn(
                    "RunTestRunner: dotnet not found while starting dotnet test for $projectFile", ex)
                return DotnetTestOutcome.Failure(
                    "Could not launch 'dotnet' for $projectFile — the dotnet CLI was not found on PATH, " +
                        "DOTNET_ROOT, or common install locations. Ensure the .NET SDK is installed and " +
                        "accessible to Rider, then retry."
                )
            }
            val completed = process.waitFor(TEST_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            if (!completed) {
                process.destroyForcibly()
                return DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
            }

            // A non-zero dotnet test exit code (failing tests) is expected and not itself a run
            // failure — only the absence of a TRX file means the run itself never completed.
            if (!trxFile.exists()) return DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")

            DotnetTestOutcome.Success(TrxParser.parse(trxFile.readText()))
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("RunTestRunner: dotnet test failed to run for $projectFile", ex)
            DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
        } finally {
            resultsDir.deleteRecursively()
        }
    }

    private fun notifyError(project: Project, message: String) {
        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) ReqnrollNotify.error(project, message, "Reqnroll: Run Test")
        }
    }
}
