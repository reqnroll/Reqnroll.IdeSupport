package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.project.Project
import com.jetbrains.rider.model.RdTargetFrameworkId
import com.jetbrains.rider.model.RunnableProject
import com.jetbrains.rider.model.runnableProjectsModel
import com.jetbrains.rider.projectView.solution
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.protocol.PackageReferenceInfo
import com.reqnroll.ide.rider.lsp.protocol.ProjectFileEntry
import com.reqnroll.ide.rider.lsp.protocol.ProjectFileRole
import com.reqnroll.ide.rider.lsp.protocol.ProjectFilesKind
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectFilesParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollProjectLoadedParams
import java.io.File
import kotlin.concurrent.thread

/**
 * Builds and (re-)sends the `reqnroll/projectLoaded` + `reqnroll/projectFiles` baseline for every
 * project in [com.jetbrains.rider.model.RunnableProjectsModel]'s *current* snapshot, on demand.
 *
 * Shared by two call sites with different timing needs:
 *  - [ReqnrollRunnableProjectsListener]/[ReqnrollProjectFilesSync] call the individual
 *    `build*`/send helpers from their own `runnableProjectsModel.projects.advise(...)`
 *    subscriptions (fired on every subsequent change).
 *  - `ReqnrollLspServerSupportProvider.fileOpened` calls [pushForAllRunnableProjects] once, right
 *    after starting the LSP server, to close a race those `ProjectActivity`-based listeners can't
 *    close on their own: they run at *project open* (independent of any file being opened) and
 *    `advise` fires immediately with whatever the snapshot is *then* — almost always before the
 *    server has actually started, since server startup itself is gated on `fileOpened` for a
 *    `.feature`/`.cs` file (see `ReqnrollLspServerSupportProvider`). That first fire finds no
 *    running server (`ReqnrollNotificationSender` logs and silently drops it) and, unlike VS's
 *    equivalent preload problem, there is no follow-up guaranteed to happen soon: the `advise`
 *    subscription only fires again on the *next* runnable-projects-model change (e.g. a rebuild),
 *    which may never come in the same session. Re-reading the current snapshot with
 *    `.valueOrNull` (a plain field read, unlike `advise` — confirmed no `assertThreading` call via
 *    decompiling `RdOptionalProperty`/`OptProperty`, so this is safe to call from any thread,
 *    including directly and synchronously from `fileOpened`) and resending is a much smaller fix
 *    than porting VS's raw named-pipe side channel: the resend is safe/idempotent by design (the
 *    server treats a repeat baseline for an already-loaded project as an update, not a duplicate
 *    — mirrors VS's own `LspProjectPreloadPusher` doc comment).
 */
object ReqnrollProjectBaseline {
    /** Sends a fresh `projectLoaded` + `projectFiles` baseline for every project currently in the runnable-projects snapshot.
     * Sends `projectLoaded` synchronously (fast, no I/O) but offloads `sendProjectFilesBaseline`
     * (which does a full filesystem walk) to a background thread so the caller's thread —
     * typically the EDT — is not blocked on disk I/O. */
    fun pushForAllRunnableProjects(project: Project) {
        val runnableProjects = project.solution.runnableProjectsModel.projects.valueOrNull.orEmpty()
        runnableProjects.forEach { runnableProject ->
            ReqnrollDebugLogger.verbose("pushForAllRunnableProjects: projectLoaded ${runnableProject.projectFilePath}")
            ReqnrollNotificationSender.sendProjectLoaded(project, buildProjectLoadedParams(project, runnableProject))
        }
        // sendProjectFilesBaseline does a synchronous walkTopDown — run on a background
        // thread so the EDT is not blocked.
        thread(name = "reqnroll-baseline-walk") {
            runnableProjects.forEach { runnableProject ->
                ReqnrollProjectBaseline.sendProjectFilesBaseline(project, runnableProject.projectFilePath)
            }
        }
    }

    /** Builds the `reqnroll/projectLoaded` params for a single [RunnableProject]. */
    fun buildProjectLoadedParams(project: Project, runnableProject: RunnableProject): ReqnrollProjectLoadedParams {
        // Reqnroll test projects normally have a single output per TFM; multi-TFM projects are
        // a known follow-up (see ReqnrollProjectFilesParams.TargetFrameworkMoniker's own
        // "Phase 1 ignores TFM" note on the server side).
        val output = runnableProject.projectOutputs.firstOrNull()
        val projectFolder = File(runnableProject.projectFilePath).parent ?: ""

        return ReqnrollProjectLoadedParams(
            workspaceFolder = project.basePath ?: "",
            projectFile = runnableProject.projectFilePath,
            projectFolder = projectFolder,
            outputAssemblyPath = output?.exePath ?: "",
            targetFrameworkMoniker = output?.tfm?.let(::toClassicMoniker) ?: "",
            // Not available from RunnableProject; server uses this only to derive namespaces for
            // scaffolded files, which isn't reachable from Rider yet anyway (no scaffolding UI).
            defaultNamespace = "",
            // No dedicated Rider model class for resolved NuGet package references (Phase 0
            // finding) — reading obj/project.assets.json from disk is the planned follow-up
            // (see the plan doc's R5), not yet implemented.
            packageReferences = emptyList<PackageReferenceInfo>(),
        )
    }

    /** Builds and sends the `reqnroll/projectFiles` baseline (kind=BASELINE) for a single project file. */
    fun sendProjectFilesBaseline(project: Project, projectFile: String) {
        val folder = File(projectFile).parent ?: return
        val files = File(folder).walkTopDown()
            .filter { it.isFile }
            .mapNotNull { file ->
                ProjectFileRole.classify(file.path)?.let { role -> ProjectFileEntry(file.path, role) }
            }
            .toList()

        ReqnrollDebugLogger.verbose("projectFiles baseline: $projectFile (${files.size} file(s))")
        ReqnrollNotificationSender.sendProjectFiles(
            project,
            ReqnrollProjectFilesParams(
                projectFile = projectFile,
                targetFrameworkMoniker = "",
                kind = ProjectFilesKind.BASELINE,
                files = files,
            ),
        )
    }

    /**
     * Builds the classic MSBuild target framework moniker (e.g. `.NETCoreApp,Version=v9.0`) the
     * server's [Reqnroll.IdeSupport.Common.ProjectSystem.TargetFrameworkMoniker] parser expects.
     *
     * [RdTargetFrameworkId.getShortName] was assumed (unverified) to hold a `net9.0`-style short
     * name, but a live devcontainer run showed the server receiving the literal string
     * `.NETCoreApp` for a net9.0 sample project — confirmed via decompiling
     * `RdTargetFrameworkId`'s real bytecode that `shortName` does not reliably hold that format.
     * `isNetCoreApp`/`isNetFramework`/`version` are typed fields on the same model class, so build
     * the moniker from those directly instead of trusting an unverified string.
     */
    internal fun toClassicMoniker(tfm: RdTargetFrameworkId): String {
        val version = tfm.version
        return when {
            tfm.isNetCoreApp -> ".NETCoreApp,Version=v${version.major}.${version.minor}"
            tfm.isNetFramework -> ".NETFramework,Version=v${version.major}.${version.minor}.${version.patch}"
            else -> tfm.shortName
        }
    }
}
