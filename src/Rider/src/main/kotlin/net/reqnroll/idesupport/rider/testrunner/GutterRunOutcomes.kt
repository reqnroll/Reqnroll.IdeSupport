package net.reqnroll.idesupport.rider.testrunner

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.UserDataHolderEx
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.components.service
import com.intellij.util.concurrency.AppExecutorUtil
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.ReqnrollServerEvents
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri
import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Outcomes of tests Rider runs itself — from its test gutter marks on a `.feature` file or its Unit Tests
 * window — shown in the `.feature` editor: the failed-step marks ([FailedStepGutterMarks]) and the results
 * the Run lens tooltips read ([RunTestResultStore]).
 *
 * Rider's test runners ignore runsettings loggers, so the backend module's `FeatureTestResultReporter` sends
 * those results to the LSP server's outcome store instead. That reporter finds the server through the session
 * breadcrumb the server's outcome listener writes when it starts, so [ensureListener] starts it on every server
 * start. When the server then pushes `reqnroll/testOutcomes/changed`, [scheduleRefresh] re-reads the outcomes of
 * every scenario in the open `.feature` editors and applies those whose server outcome changed since it last
 * looked; an unchanged outcome is not re-applied, so an edit that cleared a file's marks is not undone by a later
 * push about another file. Does nothing without the backend module, where the Run lens is the only way to run
 * a scenario and records its own outcomes.
 */
internal object GutterRunOutcomes {
    /** Pushes arrive throttled per result batch; one refresh covers every push in this window. */
    private const val REFRESH_DELAY_MS = 300L

    private val REFRESH_SCHEDULED = Key.create<AtomicBoolean>("Reqnroll.GutterRunOutcomes.refreshScheduled")

    /** The last server outcome seen per test method, keyed by [methodKey]. */
    private val lastSeen = ConcurrentHashMap<String, GetTestOutcomeResponse>()

    /** Starts the server's outcome listener (and so its session breadcrumb) now and on every later server start. */
    fun watchServerStarts(project: Project) {
        if (!BackendTestGutterMarks.backendInstalled) return
        val events = project.service<ReqnrollServerEvents>()
        events.subscribeRunning { ensureListener(project) }
        if (events.isRunning) ensureListener(project)
    }

    private fun ensureListener(project: Project) {
        ApplicationManager.getApplication().executeOnPooledThread {
            if (project.isDisposed) return@executeOnPooledThread
            val registration = ReqnrollRequestSender.registerTestRun(project)
            ReqnrollDebugLogger.info(
                "GutterRunOutcomes: outcome listener ${if (registration?.success == true) "at ${registration.endpoint}" else "not started"}")
        }
    }

    /** Refreshes the open `.feature` editors from the server's outcomes shortly after a change push. */
    fun scheduleRefresh(project: Project) {
        if (!BackendTestGutterMarks.backendInstalled || project.isDisposed) return
        val scheduled = (project as UserDataHolderEx).putUserDataIfAbsent(REFRESH_SCHEDULED, AtomicBoolean(false))
        if (!scheduled.compareAndSet(false, true)) return
        AppExecutorUtil.getAppScheduledExecutorService().schedule({
            scheduled.set(false)
            if (project.isDisposed) return@schedule
            try {
                refresh(project)
            } catch (ex: Exception) {
                ReqnrollDebugLogger.warn("GutterRunOutcomes: refreshing outcomes failed", ex)
            }
        }, REFRESH_DELAY_MS, TimeUnit.MILLISECONDS)
    }

    private fun refresh(project: Project) {
        val files = FileEditorManager.getInstance(project).openFiles
            .filter { it.extension.equals("feature", ignoreCase = true) && it.isInLocalFileSystem }
        if (files.isEmpty()) return

        val runnableProjects = project.solution.runnableProjectsModel.projects.valueOrNull.orEmpty()
        var anyChanged = false
        for (file in files) {
            val ownerPath = RunTestRunner.findOwningProjectPath(file.path, runnableProjects.map { it.projectFilePath })
            val assembly = runnableProjects.firstOrNull { it.projectFilePath == ownerPath }
                ?.let { RunTestRunner.outputAssemblyPath(it) } ?: continue
            val uri = localPathToLspUri(file.path)
            val symbols = ReqnrollRequestSender.documentSymbol(project, uri) ?: continue
            val scenarios = RunLensSupport.collectMethodSymbols(symbols).mapNotNull { symbol ->
                val line = symbol.selectionRange?.start?.line ?: return@mapNotNull null
                RunLensSupport.scenarioTargets(project, uri, symbol)
                    .takeIf { it.isNotEmpty() }
                    ?.let { ScenarioRunTarget(line, it) }
            }

            val changed = changedResults(assembly, scenarios, lastSeen) { type, method ->
                ReqnrollRequestSender.getTestOutcome(project, assembly, type, method)
            }
            if (changed.isEmpty()) continue
            anyChanged = true
            changed.forEach { (line, result) -> RunTestResultStore.set(uri, line, result) }
            FailedStepGutterMarks.update(project, uri, changed)
        }

        if (anyChanged) {
            ApplicationManager.getApplication().invokeLater {
                if (!project.isDisposed) RunTestCodeVisionProvider.refreshOpenFeatureEditors(project)
            }
        }
    }

    /**
     * The new result of each of [scenarios] whose server outcome changed since [seen] last recorded it, keyed by
     * scenario line. A scenario counts only when every one of its methods has a usable outcome (found, not stale,
     * not running — see [RunTestRunner.combineIfComplete]); its methods' outcomes are then recorded in [seen].
     * `internal` for testability.
     */
    internal fun changedResults(
        assembly: String,
        scenarios: List<ScenarioRunTarget>,
        seen: MutableMap<String, GetTestOutcomeResponse>,
        lookup: (typeFullName: String, methodName: String) -> GetTestOutcomeResponse?,
    ): Map<Int, RunResult> {
        val changed = linkedMapOf<Int, RunResult>()
        for (scenario in scenarios) {
            val methods = scenario.targets.map { it.declaringTypeFullName to it.methodName }.distinct()
            val responses = methods.map { (type, method) -> lookup(type, method) }
            val result = RunTestRunner.combineIfComplete(responses) ?: continue

            val keys = methods.map { (type, method) -> methodKey(assembly, type, method) }
            val isNew = keys.zip(responses).any { (key, response) -> seen[key] != response }
            keys.zip(responses).forEach { (key, response) -> seen[key] = response!! }
            if (isNew) changed[scenario.startLine] = result
        }
        return changed
    }

    private fun methodKey(assembly: String, type: String, method: String) =
        "${assembly.replace('\\', '/').lowercase()}|$type|$method"
}

/** Registered in `plugin.xml`: hooks [GutterRunOutcomes] up to the server's starts once a project opens. */
class GutterRunOutcomesActivity : ProjectActivity {
    override suspend fun execute(project: Project) = GutterRunOutcomes.watchServerStarts(project)
}
