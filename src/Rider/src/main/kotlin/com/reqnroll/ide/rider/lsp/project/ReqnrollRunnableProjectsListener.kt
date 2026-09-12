package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.project.Project
import com.intellij.openapi.rd.createLifetime
import com.intellij.openapi.rd.util.RdCoroutineHost
import com.intellij.openapi.startup.ProjectActivity
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectLoadedParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectUnloadedParams
import kotlinx.coroutines.withContext

/**
 * Feeds `reqnroll/projectLoaded`/`reqnroll/projectUnloaded` from Rider's own runnable-projects
 * model — [docs/Rider-Project-Document-Sync-Implementation-Plan.md] Phase 2. Confirmed via
 * decompiling Rider 2024.3.5's actual bundled classes (product.jar):
 * `project.solution.runnableProjectsModel.projects` is an `IOptProperty<List<RunnableProject>>`
 * (RD reactive property) already exposing TFM + output assembly path via
 * `RunnableProject.projectOutputs` — no RD protocol submodule needed (Phase 0 finding).
 *
 * One reactive subscription covers all three of the plan's originally-separate event sources:
 * `advise` fires immediately with the current value on subscribe (→ initial flush on project
 * open, no separate "SendInitialProjectsAsync"-equivalent needed) and again on every subsequent
 * change, including whatever Rider's backend does on rebuild (→ no separate build-completion
 * listener needed either, assuming the backend actually re-pushes this model post-build —
 * simpler than the plan's original guess of needing `RunnableProjectListener`, which turned out
 * to be Rider's own internal gutter-icon-refresh listener, not a public extension point).
 *
 * `advise` only ever hands us the *current full list* — there's no per-project add/remove
 * callback — so project removal is detected by diffing against the previous snapshot.
 *
 * Rider's backend recomputes `runnableProjectsModel.projects` repeatedly while a solution is still
 * settling (restore, target-framework resolution, etc.) — confirmed live: over a dozen re-fires
 * within a few hundred milliseconds of opening a solution, each handing back an *identical* project
 * list. Since `advise` gives no "did anything change" signal of its own, `projectLoaded` is only
 * actually (re-)sent when the built params for that project file differ from the last ones sent —
 * cheap since [ReqnrollProjectLoadedParams] is a data class — otherwise every recompute would
 * re-trigger the server's full Roslyn rediscovery/binding-registry replacement for no reason.
 */
class ReqnrollRunnableProjectsListener : ProjectActivity {
    override suspend fun execute(project: Project) {
        val lifetime = project.createLifetime()
        val tracker = ProjectLoadedTracker()

        // RD reactive properties assert they're advised from the UI thread or a scheduler the RD
        // dispatcher itself recognizes — a plain background ProjectActivity coroutine is neither,
        // and .advise() throws IllegalStateException("Wrong thread ...") without this. Confirmed
        // by decompiling Rider 2024.3.5's actual RdCoroutineHost class that .uiDispatcher is the
        // correct RD-aware context for exactly this.
        withContext(RdCoroutineHost.instance.uiDispatcher) {
            project.solution.runnableProjectsModel.projects.advise(lifetime) { runnableProjects ->
                val current = runnableProjects.orEmpty()
                val currentFiles = current.map { it.projectFilePath }.toSet()

                // advise() fires immediately at project open, independent of and almost always
                // before the server itself is started (see ReqnrollLspServerReadiness) — defer the
                // actual sends rather than dropping them straight into the server's face. Both
                // halves of tracker's state are updated inside this same deferred block (not one
                // synchronously outside it, as an earlier version did) so a second advise() firing
                // before the first's deferred action has run can't read half-updated tracking state.
                ReqnrollLspServerReadiness.runWhenRunning(project) {
                    tracker.removedSince(currentFiles).forEach { removedFile ->
                        ReqnrollDebugLogger.verbose("projectUnloaded: $removedFile")
                        ReqnrollNotificationSender.sendProjectUnloaded(project, ReqnrollProjectUnloadedParams(removedFile))
                    }

                    current.forEach { runnableProject ->
                        val params = ReqnrollProjectBaseline.buildProjectLoadedParams(project, runnableProject)
                        if (!tracker.shouldSend(runnableProject.projectFilePath, params)) return@forEach

                        ReqnrollDebugLogger.verbose("projectLoaded: ${runnableProject.projectFilePath}")
                        ReqnrollNotificationSender.sendProjectLoaded(project, params)
                    }
                }
            }
        }
    }
}

/**
 * Tracks, per project file path, the last [ReqnrollProjectLoadedParams] actually sent — so
 * [ReqnrollRunnableProjectsListener] only resends `projectLoaded` when something genuinely
 * changed (see its class doc: `advise()` re-fires with an *identical* list many times while a
 * solution settles) and can detect removals by diffing against the previous snapshot. Pure and
 * platform-independent so this diffing logic is unit-testable without an RD reactive-property
 * fixture.
 */
internal class ProjectLoadedTracker {
    private val lastSentParams = mutableMapOf<String, ReqnrollProjectLoadedParams>()

    /** Returns project file paths that were previously tracked but are absent from [currentFiles], and stops tracking them. */
    fun removedSince(currentFiles: Set<String>): Set<String> {
        val removed = lastSentParams.keys - currentFiles
        removed.forEach { lastSentParams.remove(it) }
        return removed
    }

    /** Returns `true` (and records [params] as sent) only if [params] differs from what was last sent for [projectFilePath]. */
    fun shouldSend(projectFilePath: String, params: ReqnrollProjectLoadedParams): Boolean {
        if (lastSentParams[projectFilePath] == params) return false
        lastSentParams[projectFilePath] = params
        return true
    }
}
