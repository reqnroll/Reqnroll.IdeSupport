package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.project.Project
import com.intellij.openapi.rd.createLifetime
import com.intellij.openapi.rd.util.RdCoroutineHost
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.vfs.AsyncFileListener
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.events.VFileCreateEvent
import com.intellij.openapi.vfs.newvfs.events.VFileDeleteEvent
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.openapi.vfs.newvfs.events.VFileMoveEvent
import com.intellij.openapi.vfs.newvfs.events.VFilePropertyChangeEvent
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.protocol.ProjectFileEntry
import com.reqnroll.ide.rider.lsp.protocol.ProjectFileRole
import com.reqnroll.ide.rider.lsp.protocol.ProjectFilesKind
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectFilesParams
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.concurrent.thread

/**
 * Feeds `reqnroll/projectFiles` — Phase 3 of
 * docs/Rider-Project-Document-Sync-Implementation-Plan.md. Independent `ProjectActivity` from
 * [ReqnrollRunnableProjectsListener] (Phase 2), even though both subscribe to the same
 * `runnableProjectsModel.projects` reactive property — keeps each phase self-contained rather
 * than sharing mutable state across them, at the cost of one extra (cheap) subscription.
 *
 * **Known scope reduction, deliberate:** project attribution uses longest-matching-folder-prefix
 * (mirroring VS's own `VsProjectEventMonitor.FindProjectContaining`), not a full
 * `ProjectModelEntity`/`WorkspaceModel` traversal of each project's actual MSBuild-resolved file
 * list. Rider's real project-file-membership API (confirmed to exist as
 * `com.jetbrains.rider.projectView.workspace.ProjectModelEntity`, a `WorkspaceEntity` tree) was
 * deliberately not used here: getting its traversal semantics wrong (does it include linked
 * files? correctly exclude `Compile Remove` items?) risks sending *incorrect* membership data to
 * the server, which is worse than the honest, well-understood folder-prefix approximation this
 * uses instead. That means this Phase 3, as implemented, does not yet fully solve the
 * linked/excluded-file problem `reqnroll/projectFiles` exists for (issue #32 on the VS side) —
 * only the "real-time file-event refresh instead of a static/fallback view" part. Revisit with
 * `ProjectModelEntity` if linked-file correctness turns out to matter in practice.
 */
class ReqnrollProjectFilesSync : ProjectActivity {
    override suspend fun execute(project: Project) {
        // (folder, projectFile) pairs, longest folder first so the first prefix match wins.
        // AtomicReference, not a plain var: the `advise` callback (writer) and the
        // AsyncFileListener callback (reader) can run on different threads.
        val projectFolders = java.util.concurrent.atomic.AtomicReference<List<Pair<String, String>>>(emptyList())
        // Tracks which project file paths a baseline (a full disk walk — see
        // ReqnrollProjectBaseline.sendProjectFilesBaseline) has already been sent for, so a
        // same-content re-fire of `advise` (see ReqnrollRunnableProjectsListener's doc comment on
        // why that happens repeatedly during solution load) doesn't re-walk and re-send it.
        var previousProjectFiles = emptySet<String>()

        val lifetime = project.createLifetime()

        // See ReqnrollRunnableProjectsListener's identical wrapping for why: RD reactive
        // properties assert an RD-recognized thread, which a plain background ProjectActivity
        // coroutine is not.
        withContext(RdCoroutineHost.instance.uiDispatcher) {
            project.solution.runnableProjectsModel.projects.advise(lifetime) { runnableProjects ->
                val projects = runnableProjects.orEmpty()
                projectFolders.set(
                    projects
                        .map { (File(it.projectFilePath).parent ?: "") to it.projectFilePath }
                        .sortedByDescending { it.first.length }
                )

                val currentFiles = projects.map { it.projectFilePath }.toSet()
                if (currentFiles == previousProjectFiles) return@advise
                previousProjectFiles = currentFiles

                // advise() fires immediately at project open, before the server is typically even
                // started — see ReqnrollLspServerReadiness for why this must be deferred.
                ReqnrollLspServerReadiness.runWhenRunning(project) {
                    // sendProjectFilesBaseline does a synchronous filesystem walk —
                    // run on a background thread to avoid blocking the UI dispatcher.
                    thread(name = "reqnroll-baseline-walk") {
                        projects.forEach { runnableProject ->
                            ReqnrollProjectBaseline.sendProjectFilesBaseline(project, runnableProject.projectFilePath)
                        }
                    }
                }
            }
        }

        VirtualFileManager.getInstance().addAsyncFileListener(
            { events -> prepareChange(project, events) { projectFolders.get() } },
            project,
        )
    }

    private fun prepareChange(
        project: Project,
        events: List<VFileEvent>,
        folders: () -> List<Pair<String, String>>,
    ): AsyncFileListener.ChangeApplier? {
        val changes = events.flatMap { toChanges(it) }
        if (changes.isEmpty()) return null

        // ChangeApplier's two methods (beforeVfsChange/afterVfsChange) both have default bodies
        // in Java, so it's not a single-abstract-method interface — no lambda/SAM conversion,
        // needs a real anonymous object.
        return object : AsyncFileListener.ChangeApplier {
            override fun afterVfsChange() {
                changes.groupBy { change -> findOwningProject(change.path, folders()) }
                    .forEach { (projectFile, group) ->
                        if (projectFile == null) return@forEach
                        ReqnrollDebugLogger.info("projectFiles delta: $projectFile (${group.size} change(s))")
                        ReqnrollNotificationSender.sendProjectFiles(
                            project,
                            ReqnrollProjectFilesParams(
                                projectFile = projectFile,
                                targetFrameworkMoniker = "",
                                kind = ProjectFilesKind.DELTA,
                                files = group.map { ProjectFileEntry(it.path, it.role, it.added) },
                            ),
                        )
                    }
            }
        }
    }

    /**
     * `internal` (rather than private) purely so it's unit-testable without a `Project`/VFS
     * fixture. Compares case-insensitively: two project folders differing only by case (or a
     * path arriving with different casing than the folder was registered with -- e.g. via a
     * symlink/junction) would otherwise fail this check on case-insensitive filesystems (Windows,
     * and macOS by default) even though they refer to the same location, non-deterministically
     * misattributing (or dropping) a file-change delta depending on how folders happen to sort.
     */
    internal fun findOwningProject(path: String, folders: List<Pair<String, String>>): String? =
        folders.firstOrNull { (folder, _) -> path.startsWith(folder + File.separator, ignoreCase = true) }?.second

    private data class Change(val path: String, val role: Int, val added: Boolean)

    private fun toChanges(event: VFileEvent): List<Change> = when (event) {
        is VFileCreateEvent ->
            ProjectFileRole.classify(event.path)?.let { listOf(Change(event.path, it, added = true)) }.orEmpty()

        is VFileDeleteEvent ->
            ProjectFileRole.classify(event.path)?.let { listOf(Change(event.path, it, added = false)) }.orEmpty()

        is VFileMoveEvent -> listOfNotNull(
            ProjectFileRole.classify(event.oldPath)?.let { Change(event.oldPath, it, added = false) },
            ProjectFileRole.classify(event.newPath)?.let { Change(event.newPath, it, added = true) },
        )

        is VFilePropertyChangeEvent -> if (event.isRename) {
            listOfNotNull(
                ProjectFileRole.classify(event.oldPath)?.let { Change(event.oldPath, it, added = false) },
                ProjectFileRole.classify(event.newPath)?.let { Change(event.newPath, it, added = true) },
            )
        } else {
            emptyList()
        }

        else -> emptyList()
    }
}
