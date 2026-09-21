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
import com.reqnroll.ide.rider.lsp.ReqnrollMtpReporterPathResolver
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
 * so a server lookup that comes back empty, stale, or incomplete (server not running, logger not
 * bundled correctly, the runner process exited before the server finished processing — see
 * [combineIfComplete]) falls back to the pre-#700 TRX-only result exactly as before. This fallback is deliberately kept for at least one release
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
                // command line below (VsTest mode) or discoverable via the LSP server's session
                // breadcrumb (MTP modes — see EnsureStarted's side effect on the server, plan §4).
                // A null/unsuccessful registration (no server running, or it couldn't start its
                // listener) just means the extra arguments/injection are omitted — the run
                // proceeds exactly as it did before #700, TRX-only (VsTest) or exit-code-only (MTP).
                val registration = ReqnrollRequestSender.registerTestRun(project)
                    ?.takeIf { it.success }
                val mode = detectDotnetTestMode(runnableProject.projectFilePath)
                val loggerDirectory = registration?.takeIf { mode == DotnetTestMode.VS_TEST }?.let { ReqnrollTestLoggerPathResolver.resolve() }
                val reporterInjected = registration != null && mode != DotnetTestMode.VS_TEST &&
                    ReqnrollMtpReporterPathResolver.resolve() != null

                val testOutcome = runDotnetTest(runnableProject.projectFilePath, filter, registration, loggerDirectory, mode)
                val fallbackResult = when (testOutcome) {
                    is DotnetTestOutcome.Failure -> {
                        notifyError(project, testOutcome.message)
                        return
                    }
                    is DotnetTestOutcome.Inconclusive -> {
                        // No modal here (unlike Failure): this fires on every Run click for a
                        // project this ad hoc scan missed classifying as MTP upfront, and a dialog
                        // on every click would itself be the regression #715 phase 3 fixed.
                        ReqnrollDebugLogger.warn("RunTestRunner: ${testOutcome.message}")
                        return
                    }
                    is DotnetTestOutcome.Success -> trxResult(testOutcome.results)
                    // MTP modes (issue #715 phase 4): no TRX exists by design, so the process's own
                    // exit code is the only always-available signal — live-verified against a real
                    // native-mode dotnet test. Per-row detail, when available, comes from the
                    // server poll below, same as the VsTest server-pipeline path.
                    is DotnetTestOutcome.ExitCodeOnly ->
                        RunResult(if (testOutcome.exitCode == 0) RunOutcome.PASSED else RunOutcome.FAILED)
                }

                val assemblyPath = registration?.let { outputAssemblyPath(runnableProject) }
                val result = if (assemblyPath != null && (loggerDirectory != null || reporterInjected)) {
                    pollServerResult(project, assemblyPath, targets) ?: fallbackResult
                } else {
                    fallbackResult
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
     * `dotnet test` process itself exits. Returns null (fall back to TRX) unless *every* method
     * has a usable outcome ([combineIfComplete]) within the attempts. Runs on the calling
     * (background task) thread — never call from the EDT.
     */
    private fun pollServerResult(project: Project, assemblyPath: String, targets: List<ScenarioTestTargetItem>): RunResult? {
        val distinctMethods = targets.map { it.declaringTypeFullName to it.methodName }.distinct()
        repeat(OUTCOME_POLL_ATTEMPTS) { attempt ->
            val responses = distinctMethods.map { (type, method) ->
                ReqnrollRequestSender.getTestOutcome(project, assemblyPath, type, method)
            }
            combineIfComplete(responses)?.let { return it }
            if (attempt < OUTCOME_POLL_ATTEMPTS - 1) Thread.sleep(OUTCOME_POLL_DELAY_MS)
        }
        return null
    }

    /**
     * One poll attempt's verdict: [combineServerOutcomes] over [responses] only when every one is
     * usable for *this* run, else null (keep polling / fall back to TRX). "Usable" means
     * [GetTestOutcomeResponse.found] and neither [GetTestOutcomeResponse.isStale] nor
     * [GetTestOutcomeResponse.isRunning]: the server's `TryGet` happily returns the *previous*
     * run's outcome for a method (`Found = true`) with `IsStale = true` once this run's build has
     * rewritten the container, so accepting `found` alone would report last time's glyph as this
     * run's whenever this run's own result hasn't landed yet — or never does (logger failed to
     * load/connect). Requiring every method, not just one, keeps a multi-target scenario from
     * being summarized off a partial set (method A recorded, B still in flight) — the same
     * `Failed` > `Passed` precedence as [combineServerOutcomes] is meaningless over half the rows.
     * `internal` for testability.
     */
    internal fun combineIfComplete(responses: List<GetTestOutcomeResponse?>): RunResult? {
        if (responses.isEmpty()) return null
        val usable = responses.map { it?.takeIf { r -> r.found && !r.isStale && !r.isRunning } ?: return null }
        return combineServerOutcomes(usable)
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

        /**
         * No TRX was produced, but [looksLikeMtpProject] says this is an MTP-mode project our
         * upfront [detectDotnetTestMode] scan missed (plan §7 risk #5's blind spots — a
         * condition-guarded property, or one set only via an `Import`) — not a genuine launch
         * failure. Distinguished from [Failure] so the call site logs instead of showing a *modal*
         * error dialog on every single Run click for a project this plugin misclassified.
         */
        data class Inconclusive(val message: String) : DotnetTestOutcome()

        /**
         * [DotnetTestMode.MTP_COMPAT]/[DotnetTestMode.MTP_NATIVE]'s *expected* outcome shape (issue
         * #715 phase 4): no TRX exists by design in either mode — the run never asked for one —
         * so the process's own exit code (0 = every test passed) is the only signal always
         * available without the LSP-server pipeline. Live-verified: a real native-mode `dotnet
         * test` exits 0 when its (filtered) selection all pass, non-zero otherwise, same contract
         * VSTest's own exit code already has.
         */
        data class ExitCodeOnly(val exitCode: Int) : DotnetTestOutcome()
    }

    /**
     * Shells to `dotnet test --filter`, shaped per [mode] (issue #715 phase 4):
     * - [DotnetTestMode.VS_TEST]: a TRX logger, plus — when [registration] succeeded and
     *   [loggerDirectory] is non-null — the bundled `Reqnroll.IdeSupport.TestLogger` alongside it.
     *   Parses the TRX result; [DotnetTestOutcome.Failure] means the run itself couldn't be
     *   started/completed (a non-zero exit code from failing tests is not itself a failure, only
     *   the absence of a TRX file is).
     * - [DotnetTestMode.MTP_COMPAT]/[DotnetTestMode.MTP_NATIVE]: neither `--logger` nor
     *   `--test-adapter-path` — live-verified: MTP-compat mode silently ignores them, native mode
     *   treats `--logger` as an unrecognized option and hard-fails the whole run with exit code 5.
     *   Instead, when [registration] succeeded and the bundled MTP reporter is found
     *   ([ReqnrollMtpReporterPathResolver]), ephemerally injects it via the
     *   `CustomAfterMicrosoftCommonTargets` MSBuild extensibility point (plan §5.6) — live-verified
     *   end to end: the generated `SelfRegisteredExtensions.g.cs` picks up the injected
     *   `TestingPlatformBuilderHook` item with zero changes to the project file itself, and the
     *   reporter's own DLL is copied to the build output alongside its dependency. Returns
     *   [DotnetTestOutcome.ExitCodeOnly] rather than parsing a TRX that was never requested.
     */
    private fun runDotnetTest(
        projectFile: String,
        filter: String,
        registration: RegisterTestRunResponse?,
        loggerDirectory: Path?,
        mode: DotnetTestMode,
    ): DotnetTestOutcome {
        val resultsDir = Files.createTempDirectory("reqnroll-test-").toFile()
        val trxFileName = "result.trx"
        val trxFile = File(resultsDir, trxFileName)
        var ephemeralInjectionDir: File? = null

        return try {
            val command = mutableListOf(DotnetCliLocator.resolve(), "test", projectFile, "--filter", filter, "--nologo")

            when (mode) {
                DotnetTestMode.VS_TEST -> {
                    command += listOf(
                        "--logger", "trx;LogFileName=$trxFileName",
                        "--results-directory", resultsDir.absolutePath,
                    )
                    if (registration != null && loggerDirectory != null) {
                        command += listOf(
                            "--test-adapter-path", loggerDirectory.toString(),
                            "--logger", buildLoggerArgument(registration, ProcessHandle.current().pid()),
                        )
                    }
                }
                DotnetTestMode.MTP_COMPAT, DotnetTestMode.MTP_NATIVE -> {
                    // Nothing added here: outcomes for these modes come from the ephemerally
                    // injected reporter reporting to the LSP server directly (below), not from
                    // anything on this command line.
                }
            }

            val processBuilder = ProcessBuilder(command)
                // Required for global.json (native MTP mode) discovery, which the .NET SDK
                // resolves relative to the *working directory*, not the project file's own
                // directory — live-verified: without this, dotnet test falls back to legacy
                // VSTest-mode detection and hard-errors on an MTP-capable/.NET-10-SDK project
                // ("Testing with VSTest target is no longer supported..."), even though the
                // project's own global.json correctly opts into native mode. An independent fix
                // from the mode-detection/injection work above — this plugin's dotnet test
                // shell-out never set a working directory at all before phase 4.
                .directory(File(projectFile).parentFile)
            if (mode != DotnetTestMode.VS_TEST && registration != null) {
                ReqnrollMtpReporterPathResolver.resolve()?.let { reporterDll ->
                    val preExisting = processBuilder.environment()["CustomAfterMicrosoftCommonTargets"]
                    val targetsFile = writeEphemeralMtpTargetsFile(reporterDll, preExisting)
                    ephemeralInjectionDir = targetsFile.parentFile
                    processBuilder.environment()["CustomAfterMicrosoftCommonTargets"] = targetsFile.path
                }
            }

            // Neither stdout nor stderr is read anywhere (results come from the TRX file or the
            // exit code, not live process output) — Redirect.DISCARD avoids the classic
            // ProcessBuilder deadlock where an un-drained pipe fills its OS buffer and the child
            // blocks writing to it, making `waitFor` hang until the timeout even for a run that
            // would otherwise succeed.
            val process = try {
                processBuilder
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

            if (mode != DotnetTestMode.VS_TEST) {
                return DotnetTestOutcome.ExitCodeOnly(process.exitValue())
            }

            // A non-zero dotnet test exit code (failing tests) is expected and not itself a run
            // failure — only the absence of a TRX file means the run itself never completed. A
            // project this ad hoc scan missed classifying as MTP upfront (plan §7 risk #5's blind
            // spots) is the one case where "no TRX" doesn't mean that: report it as Inconclusive,
            // not Failure, so the call site doesn't show a false error on a run whose tests may
            // well have passed.
            if (!trxFile.exists()) {
                return if (looksLikeMtpProject(projectFile))
                    DotnetTestOutcome.Inconclusive(
                        "dotnet test produced no TRX file for $projectFile — this project uses " +
                            "Microsoft.Testing.Platform (MTP), whose dotnet test integration this " +
                            "plugin doesn't support live results for yet (issue #715)."
                    )
                else
                    DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
            }

            DotnetTestOutcome.Success(TrxParser.parse(trxFile.readText()))
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("RunTestRunner: dotnet test failed to run for $projectFile", ex)
            DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
        } finally {
            resultsDir.deleteRecursively()
            ephemeralInjectionDir?.deleteRecursively()
        }
    }

    /**
     * Random, permanent identifier for this hook registration (issue #715 plan §5.6/§7's "Include
     * GUID is a random identifier — never copy one from another extension's props file" note) —
     * must match the same literal value the phase-2 test fixture
     * (`tests/Core/TestReporterFixtures/MsTestReqnrollMtp/MsTestReqnrollMtp.Fixture.csproj`) and any
     * future NuGet-packaged `buildMultiTargeting` props file declares for this same hook.
     */
    private const val MTP_REPORTER_HOOK_GUID = "a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c"

    /**
     * Writes a small, distinctly-named `.targets` file (plan §5.6) declaring a `HintPath`
     * `<Reference>` to the bundled MTP reporter plus the `<TestingPlatformBuilderHook>` item that
     * gets it auto-registered via MTP's own `SelfRegisteredExtensions` generation — never touching
     * the user's own project file. Live-verified: `CustomAfterMicrosoftCommonTargets` pointed at
     * this file is enough on its own, with zero project-file changes, for the generated entry point
     * to call into the reporter's hook and for its DLL (and its
     * `Reqnroll.IdeSupport.TestReporter.Common` dependency) to land in the build output.
     *
     * [preExistingCustomAfterTargets], when non-null, is chain-imported first (plan §7 risk #6):
     * `CustomAfterMicrosoftCommonTargets` is a general-purpose MSBuild extensibility slot a repo
     * could already be using for something unrelated — overwriting it outright would silently break
     * that customization for the duration of this one build. `Exists(...)` guards the import so a
     * value that happened to be a stale/invalid path doesn't itself break the build.
     */
    private fun writeEphemeralMtpTargetsFile(reporterDllPath: Path, preExistingCustomAfterTargets: String?): File {
        val dir = Files.createTempDirectory("reqnroll-mtp-inject-").toFile()
        val file = File(dir, "Reqnroll.IdeSupport.TestReporter.MTP.g.targets")
        val chainImport = preExistingCustomAfterTargets
            ?.takeIf { it.isNotBlank() }
            ?.let { "  <Import Project=\"$it\" Condition=\"Exists('$it')\" />\n" }
            .orEmpty()
        file.writeText(
            "<Project>\n" +
                chainImport +
                "  <ItemGroup>\n" +
                "    <Reference Include=\"Reqnroll.IdeSupport.TestReporter.MTP\">\n" +
                "      <HintPath>$reporterDllPath</HintPath>\n" +
                "    </Reference>\n" +
                "    <TestingPlatformBuilderHook Include=\"$MTP_REPORTER_HOOK_GUID\">\n" +
                "      <DisplayName>Reqnroll.IdeSupport.TestReporter.MTP</DisplayName>\n" +
                "      <TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>\n" +
                "    </TestingPlatformBuilderHook>\n" +
                "  </ItemGroup>\n" +
                "</Project>\n"
        )
        return file
    }

    private fun notifyError(project: Project, message: String) {
        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) ReqnrollNotify.error(project, message, "Reqnroll: Run Test")
        }
    }

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

    /**
     * Ad hoc, narrow mode detection (issue #715 plan §5.7/§6 phase 4: *"this phase can ship with a
     * narrower, ad hoc version of that detection, since the full mode-detection machinery isn't
     * needed yet"* — phase 3's wording, graduated here into the real three-way state) — a plain
     * text scan of [projectFilePath] itself and every `Directory.Build.props`/`global.json` found
     * walking up from its folder to the nearest `.git`/`.sln`. Not a full MSBuild evaluation
     * (misses e.g. a condition-guarded property, or one set only via an `Import`), but enough to
     * catch the common case Microsoft's own docs recommend — setting the property once in a
     * repo-root `Directory.Build.props` — without needing a generic MSBuild-property read off
     * Rider's `RunnableProject` model, which has none (plan §5.7 risk #7: unlike VS's
     * `project.Properties.Item(name)` or VS Code's `-getProperty:`, Rider's TFM read goes through a
     * narrower, purpose-built RD-protocol field — confirmed by
     * [com.reqnroll.ide.rider.lsp.project.ReqnrollProjectBaseline]'s own `toClassicMoniker`
     * workaround for that same gap; still unconfirmed whether a generic path exists at all).
     *
     * Combines two independent signals exactly as plan §5.7 describes: [MTP_CAPABLE_PATTERN]
     * (per-project) and, if capable, which of [GLOBAL_JSON_MTP_RUNNER_PATTERN] (workspace-level —
     * native mode, checked/overridden by the `DOTNET_TEST_RUNNER` env var per its own documented
     * precedence) or [DOTNET_TEST_MTP_REDIRECT_PATTERN] (per-project — legacy `dotnet test`
     * redirect) applies. Plan §7 risk #5's warning is the reason capability alone isn't enough: a
     * project can set `EnableMSTestRunner`/`EnableNUnitRunner` and still run under plain VSTest
     * today (TRX written normally, native-mode CLI shape never used) if neither is active.
     * `internal` for testability.
     */
    internal fun detectDotnetTestMode(projectFilePath: String): DotnetTestMode {
        val projectFile = File(projectFilePath)
        var mtpCapable = readTextOrNull(projectFile)?.let(MTP_CAPABLE_PATTERN::containsMatchIn) == true
        var dotnetTestRedirectsToMtp = readTextOrNull(projectFile)?.let(DOTNET_TEST_MTP_REDIRECT_PATTERN::containsMatchIn) == true
        var nativeModeActive = false

        var dir = projectFile.parentFile
        while (dir != null) {
            readTextOrNull(File(dir, "Directory.Build.props"))?.let { props ->
                if (MTP_CAPABLE_PATTERN.containsMatchIn(props)) mtpCapable = true
                if (DOTNET_TEST_MTP_REDIRECT_PATTERN.containsMatchIn(props)) dotnetTestRedirectsToMtp = true
            }
            readTextOrNull(File(dir, "global.json"))?.let { json ->
                if (GLOBAL_JSON_MTP_RUNNER_PATTERN.containsMatchIn(json)) nativeModeActive = true
            }
            if (File(dir, ".git").exists()) break // Workspace root reached; stop climbing.
            dir = dir.parentFile
        }

        // DOTNET_TEST_RUNNER (.NET 11 Preview 6+) overrides global.json for the current process
        // when recognized. This repo's own SDK (10.0.401) predates it, so this branch is
        // forward-looking and not live-verified — safe regardless, since an unset/unrecognized
        // value leaves the global.json-derived signal above untouched.
        when (System.getenv("DOTNET_TEST_RUNNER")?.trim()?.lowercase()) {
            "microsoft.testing.platform" -> nativeModeActive = true
            "vstest" -> nativeModeActive = false
        }

        return when {
            !mtpCapable -> DotnetTestMode.VS_TEST
            nativeModeActive -> DotnetTestMode.MTP_NATIVE
            dotnetTestRedirectsToMtp -> DotnetTestMode.MTP_COMPAT
            else -> DotnetTestMode.VS_TEST
        }
    }

    /** True for either MTP state — the reactive safety-net check inside [runDotnetTest]'s VsTest branch uses this, not the full [DotnetTestMode]. See [detectDotnetTestMode]. */
    internal fun looksLikeMtpProject(projectFilePath: String): Boolean =
        detectDotnetTestMode(projectFilePath) != DotnetTestMode.VS_TEST

    private fun readTextOrNull(file: File): String? =
        try {
            if (file.isFile) file.readText() else null
        } catch (ex: java.io.IOException) {
            null // Unreadable (permissions, mid-write): treat as "no evidence", not a crash.
        }
}

/**
 * Which `dotnet test` shape a project needs (issue #715 plan §5.7) — see
 * [RunTestRunner.detectDotnetTestMode].
 */
internal enum class DotnetTestMode { VS_TEST, MTP_COMPAT, MTP_NATIVE }
