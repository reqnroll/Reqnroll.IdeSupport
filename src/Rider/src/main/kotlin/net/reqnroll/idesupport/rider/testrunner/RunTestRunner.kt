package net.reqnroll.idesupport.rider.testrunner

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.jetbrains.rider.model.RunnableProject
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import net.reqnroll.idesupport.rider.actions.ReqnrollNotify
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.lspUriToLocalPath
import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ScenarioTestTargetItem
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Runs the scenarios behind a "▶ Run" lens or gutter click with `dotnet test --filter`, the same approach
 * as VS Code. The plugin has no `com.intellij.execution` test-runner integration to delegate to (see
 * [RunTestCodeVisionProvider]). See Test-Runner-Integration-Design, "Run and Debug per IDE".
 *
 * The work is split by concern:
 * - this object resolves the owning project, builds the filter, launches and waits for the process, and
 *   publishes the result;
 * - [DotnetTestModeDetector] picks the `dotnet test` mode;
 * - [DotnetTestModeStrategy] shapes the command line and reads the result for that mode;
 * - [RunOutcomeCollector] fetches per-method outcomes from the LSP server and combines them, falling back
 *   to the run's own TRX or exit-code result.
 */
object RunTestRunner {
    /**
     * Runs the resolved [targets] on a background task and updates [RunTestResultStore] and the lens when
     * it completes. For a Feature/Rule "Run Scenarios" run, [scenarios] lists each contained scenario's
     * lens line and targets so each scenario lens also gets its own outcome; it is empty for a
     * single-scenario run.
     */
    fun run(
        project: Project,
        uri: String,
        startLine: Int,
        targets: List<ScenarioTestTargetItem>,
        scenarios: List<ScenarioRunTarget> = emptyList(),
    ) {
        ReqnrollDebugLogger.info(
            "RunTestRunner: invoked for $uri:$startLine (${targets.size} target(s))")

        ProgressManager.getInstance().run(object : Task.Backgroundable(project, "Reqnroll: Running Test", true) {
            override fun run(indicator: ProgressIndicator) {
                val runnableProject = resolveRunnableProject(project, uri) ?: return
                val projectFile = runnableProject.projectFilePath

                val filter = buildTestFilter(targets)
                if (filter.isEmpty()) {
                    notifyError(project, "No test target(s) resolved for this scenario.")
                    return
                }

                // `dotnet test` builds from the files on disk, so unsaved editor edits must reach disk
                // first (as Rider's own run configurations do) or the run exercises stale content.
                saveAllDocuments()

                // Register before running: VSTest mode bakes the endpoint into the command line, and the
                // MTP reporter finds it through the server's session breadcrumb. Without a registration
                // the run still goes ahead, with the TRX or exit code as its only result.
                val registration = ReqnrollRequestSender.registerTestRun(project)?.takeIf { it.success }
                val strategy = DotnetTestModeStrategy.create(DotnetTestModeDetector.detect(projectFile), registration)

                val fallbackResult = when (val outcome = runDotnetTest(projectFile, filter, strategy) { indicator.isCanceled }) {
                    is DotnetTestOutcome.Completed -> outcome.result
                    is DotnetTestOutcome.Failure -> {
                        notifyError(project, outcome.message)
                        return
                    }
                    is DotnetTestOutcome.Cancelled -> {
                        // The user pressed Cancel: not an error, so no notification.
                        ReqnrollDebugLogger.info("RunTestRunner: dotnet test cancelled by the user")
                        return
                    }
                    is DotnetTestOutcome.Inconclusive -> {
                        // Logged, not shown: it repeats on every Run click for the project, and a modal
                        // each time would be worse than no result.
                        ReqnrollDebugLogger.warn("RunTestRunner: ${outcome.message}")
                        return
                    }
                }

                val serverOutcomes = if (strategy.reportsToServer) {
                    outputAssemblyPath(runnableProject)?.let { RunOutcomeCollector.pollServerOutcomes(project, it, targets) }
                } else {
                    null
                }
                publishResults(project, uri, startLine, scenarios, serverOutcomes, fallbackResult)
            }
        })
    }

    /** The runnable project that owns the `.feature` file at [uri], or null after telling the user why there is none. */
    private fun resolveRunnableProject(project: Project, uri: String): RunnableProject? {
        val filePath = lspUriToLocalPath(uri)
        if (filePath == null) {
            notifyError(project, "Could not resolve a local path for $uri.")
            return null
        }
        val runnableProjects = project.solution.runnableProjectsModel.projects.valueOrNull.orEmpty()
        val ownerPath = findOwningProjectPath(filePath, runnableProjects.map { it.projectFilePath })
        val runnableProject = runnableProjects.firstOrNull { it.projectFilePath == ownerPath }
        if (runnableProject == null) notifyError(project, "Could not find the project that owns $filePath.")
        return runnableProject
    }

    /**
     * Stores the run's result for the clicked lens and, for a Feature/Rule run, for each contained
     * scenario; updates the failed-step gutter marks; and refreshes the open editors.
     */
    private fun publishResults(
        project: Project,
        uri: String,
        startLine: Int,
        scenarios: List<ScenarioRunTarget>,
        serverOutcomes: Map<Pair<String, String>, GetTestOutcomeResponse>?,
        fallbackResult: RunResult,
    ) {
        val result = serverOutcomes?.let { RunOutcomeCollector.combineServerOutcomes(it.values.toList()) } ?: fallbackResult
        val perScenario = RunOutcomeCollector.scenarioResults(scenarios, serverOutcomes, fallbackResult)

        RunTestResultStore.set(uri, startLine, result)
        for ((scenarioLine, scenarioResult) in perScenario) {
            if (scenarioLine != startLine) RunTestResultStore.set(uri, scenarioLine, scenarioResult)
        }

        // Failed-step marks: one scenario for a plain run, each contained scenario for a Feature/Rule run
        // (the container's own line is not a scenario header). A scenario without its own result still
        // gets an entry, so a mark left by an earlier run is cleared rather than left describing stale state.
        val scenarioLines = if (scenarios.isEmpty()) {
            mapOf(startLine to result)
        } else {
            scenarios.associate { it.startLine to RunResult(RunOutcome.FAILED) } + perScenario
        }
        FailedStepGutterMarks.update(project, uri, scenarioLines)

        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) RunTestCodeVisionProvider.refreshOpenFeatureEditors(project)
        }
    }

    /**
     * The project file path (from [projectFilePaths]) whose folder contains [filePath], deepest match
     * first, like VS Code's `findOwningProjectFile`. Takes plain paths rather than [RunnableProject] (an
     * RD protocol type that a unit test cannot practically construct) so the matching stays testable.
     */
    internal fun findOwningProjectPath(filePath: String, projectFilePaths: Collection<String>): String? {
        // Pitfall: [lspUriToLocalPath] yields forward-slash paths ("W:/repo/A.feature") while the project
        // model reports platform paths ("W:\repo\A.csproj" on Windows). Both are normalised to '/'; a
        // prefix built from File.separatorChar never matches on Windows.
        val normalizedFile = filePath.replace('\\', '/')
        var best: String? = null
        var bestLen = 0
        for (projectFilePath in projectFilePaths) {
            val normalized = projectFilePath.replace('\\', '/')
            val slash = normalized.lastIndexOf('/')
            if (slash < 0) continue
            val prefix = normalized.substring(0, slash).trimEnd('/') + '/'
            if (normalizedFile.startsWith(prefix, ignoreCase = true) && prefix.length > bestLen) {
                best = projectFilePath
                bestLen = prefix.length
            }
        }
        return best
    }

    /**
     * A `dotnet test --filter` expression covering every distinct generated method among [targets].
     * Row-test targets that share a method collapse to one term, since running the method runs every
     * row; individual-method targets each get their own. The same dedup as VS Code's
     * `testFilterBuilder.ts` and VS's `TestMethodIdentifier`.
     */
    internal fun buildTestFilter(targets: List<ScenarioTestTargetItem>): String =
        targets
            .map { "${it.declaringTypeFullName}.${it.methodName}" }
            .distinct()
            .joinToString("|") { "FullyQualifiedName=$it" }

    /**
     * The compiled test assembly for [runnableProject]. The VSTest logger reports it as the result's
     * `Source` and the MTP reporter as `Assembly.GetEntryAssembly()?.Location`, and the server needs it
     * to look outcomes up (type and method alone are not unique across assemblies). Rider's project
     * model has no "test output assembly"; [ProjectOutput.exePath] is the built-artifact path the
     * platform's own run infrastructure uses. Returns null, which skips the server lookup, rather than
     * guessing when a project has no recorded output.
     */
    internal fun outputAssemblyPath(runnableProject: RunnableProject): String? =
        runnableProject.projectOutputs.firstOrNull()?.exePath
            ?.takeIf { it.isNotBlank() }
            ?.let(::normalizeToManagedAssemblyPath)

    /**
     * [rawExePath] if it names a `.dll`, else its `.dll` sibling when one exists on disk.
     *
     * Pitfall: an `OutputType=Exe` test project (which `xunit.v3` forces for MTP) gets a native apphost,
     * and `exePath` points at that apphost rather than the managed `.dll`. The reporter always reports
     * the `.dll`, so server lookups by the apphost path never match: the lens glyph still updates from
     * the exit code, but the hover never shows failed-step detail. The SDK names the apphost after the
     * assembly, in the same folder, so the sibling is the right file. When it does not exist, the
     * original path is returned rather than one that is certainly wrong.
     */
    internal fun normalizeToManagedAssemblyPath(rawExePath: String): String {
        if (rawExePath.endsWith(".dll", ignoreCase = true)) return rawExePath
        val exeFile = File(rawExePath)
        val dllSibling = File(exeFile.parentFile, exeFile.nameWithoutExtension + ".dll")
        return if (dllSibling.exists()) dllSibling.path else rawExePath
    }

    /**
     * Runs `dotnet test <projectFile> --filter <filter>` with [strategy]'s arguments, waits for it, and
     * lets [strategy] read the result. The launch, the timeout and cancellation are the same in every mode.
     */
    private fun runDotnetTest(
        projectFile: String,
        filter: String,
        strategy: DotnetTestModeStrategy,
        isCanceled: () -> Boolean,
    ): DotnetTestOutcome = try {
        val command = listOf(DotnetCliLocator.resolve(), "test", projectFile, "--filter", filter) + strategy.arguments()
        strategy.prepare(projectFile)

        val process = try {
            ProcessBuilder(command)
                // The .NET SDK finds global.json (and so native MTP mode) relative to the working
                // directory, not the project file. Without this, dotnet test falls back to VSTest
                // detection and an MTP project on the .NET 10 SDK fails with "Testing with VSTest target
                // is no longer supported...".
                .directory(File(projectFile).parentFile)
                // Nothing reads the output (results come from the TRX or the exit code). Discarding it
                // avoids the ProcessBuilder deadlock where an undrained pipe fills and the child blocks,
                // so waitFor hangs until the timeout.
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

        // The budget covers the build as well as the tests (`dotnet test` builds first), so it is
        // overridable (see DotnetTestProcessWaiter.TIMEOUT_PROPERTY) and a timeout says so.
        val timeoutSeconds = DotnetTestProcessWaiter.resolveTimeoutSeconds()
        when (DotnetTestProcessWaiter.await(process, TimeUnit.SECONDS.toMillis(timeoutSeconds), isCanceled)) {
            ProcessWaitResult.COMPLETED -> strategy.interpret(projectFile, process.exitValue())
            ProcessWaitResult.CANCELLED -> DotnetTestOutcome.Cancelled
            ProcessWaitResult.TIMED_OUT -> DotnetTestOutcome.Failure(
                "dotnet test for $projectFile did not finish within $timeoutSeconds seconds and was stopped. " +
                    "The time includes building the project, so a large solution may need longer: set the " +
                    "-D${DotnetTestProcessWaiter.TIMEOUT_PROPERTY}=<seconds> JVM option to raise the limit."
            )
        }
    } catch (ex: Exception) {
        ReqnrollDebugLogger.warn("RunTestRunner: dotnet test failed to run for $projectFile", ex)
        DotnetTestOutcome.Failure("dotnet test failed to run for $projectFile.")
    } finally {
        strategy.cleanUp()
    }

    /** Flushes every unsaved editor document to disk on the EDT; a failure is logged and the run proceeds against what is on disk. */
    private fun saveAllDocuments() {
        try {
            ApplicationManager.getApplication().invokeAndWait {
                FileDocumentManager.getInstance().saveAllDocuments()
            }
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("RunTestRunner: saving open documents before dotnet test failed", ex)
        }
    }

    private fun notifyError(project: Project, message: String) {
        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) ReqnrollNotify.error(project, message, "Reqnroll: Run Test")
        }
    }
}
