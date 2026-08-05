package com.reqnroll.ide.rider.testrunner

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.vfs.VirtualFileManager
import com.jetbrains.rider.model.RunnableProject
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import java.io.File
import java.nio.file.Files
import java.util.concurrent.TimeUnit

/**
 * Implements the "▶ Run" lens click (design doc §5/§6, issue #262) — Rider's own execution of
 * `dotnet test --filter`, mirroring VS Code's already-shipped "Option 2" for this same issue
 * (this plugin has no native `com.intellij.execution`/Test Runner integration to delegate to
 * instead — see [RunTestCodeVisionProvider]'s doc comment).
 */
object RunTestRunner {
    private const val TEST_TIMEOUT_SECONDS = 120L

    /** Runs the resolved [targets] on a background task and updates [RunTestResultStore]/the lens once it completes. */
    fun run(project: Project, uri: String, startLine: Int, targets: List<ScenarioTestTargetItem>) {
        ReqnrollDebugLogger.info("RunTestRunner: invoked for $uri:$startLine (${targets.size} target(s))")

        ProgressManager.getInstance().run(object : Task.Backgroundable(project, "Reqnroll: Running Test", true) {
            override fun run(indicator: ProgressIndicator) {
                val filePath = VirtualFileManager.getInstance().findFileByUrl(uri)?.path
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

                val results = runDotnetTest(runnableProject.projectFilePath, filter)
                if (results == null) {
                    notifyError(project, "dotnet test failed to run for ${runnableProject.projectFilePath}.")
                    return
                }

                val outcome = if (results.any { it.outcome == "Failed" }) RunOutcome.FAILED else RunOutcome.PASSED
                RunTestResultStore.set(uri, startLine, RunResult(outcome))

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

    /** Shells to `dotnet test --filter` with a TRX logger and parses the result. Returns null when the run itself couldn't be started/completed — a non-zero exit code from failing tests is not itself a failure, only the absence of a TRX file is. */
    private fun runDotnetTest(projectFile: String, filter: String): List<TrxUnitTestResult>? {
        val resultsDir = Files.createTempDirectory("reqnroll-test-").toFile()
        val trxFileName = "result.trx"
        val trxFile = File(resultsDir, trxFileName)

        return try {
            val command = listOf(
                "dotnet", "test", projectFile,
                "--filter", filter,
                "--logger", "trx;LogFileName=$trxFileName",
                "--results-directory", resultsDir.absolutePath,
                "--nologo",
            )
            val process = ProcessBuilder(command)
                .redirectErrorStream(true)
                .start()
            val completed = process.waitFor(TEST_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            if (!completed) {
                process.destroyForcibly()
                return null
            }

            // A non-zero dotnet test exit code (failing tests) is expected and not itself a run
            // failure — only the absence of a TRX file means the run itself never completed.
            if (!trxFile.exists()) return null

            TrxParser.parse(trxFile.readText())
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("RunTestRunner: dotnet test failed to run for $projectFile", ex)
            null
        } finally {
            resultsDir.deleteRecursively()
        }
    }

    private fun notifyError(project: Project, message: String) {
        ReqnrollDebugLogger.warn("RunTestRunner: $message")
        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) Messages.showErrorDialog(project, message, "Reqnroll: Run Test")
        }
    }
}
