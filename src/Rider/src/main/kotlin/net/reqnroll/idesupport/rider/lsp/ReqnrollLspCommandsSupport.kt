package net.reqnroll.idesupport.rider.lsp

import com.google.gson.JsonPrimitive
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.customization.LspCommandsSupport
import net.reqnroll.idesupport.rider.actions.FindStepUsagesRunner
import net.reqnroll.idesupport.rider.actions.GoToHooksRunner
import net.reqnroll.idesupport.rider.actions.GoToMatchingScenariosRunner
import net.reqnroll.idesupport.rider.actions.ReqnrollNotify
import net.reqnroll.idesupport.rider.codevision.HookLensSupport
import net.reqnroll.idesupport.rider.codevision.NativeLspCodeLens
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
import org.eclipse.lsp4j.Command

/**
 * Runs the server's *client-side* CodeLens commands (`reqnroll.findStepUsages`,
 * `reqnroll.goToHooks`, `reqnroll.goToMatchingScenarios`, `reqnroll.noStepUsages`) when a lens is
 * clicked.
 *
 * Rider 2026.2's generic LSP client renders `textDocument/codeLens` natively and, on a click, hands
 * the lens's [Command] to `LspCommandsSupport.executeCommand` — whose default sends it back to the
 * server as `workspace/executeCommand`. These names are not server commands (the server only
 * registers `reqnroll.toggleComment` and a few code-action commands), so by default clicking such a
 * lens did nothing. Earlier Rider versions never rendered these lenses natively, so this is only
 * reached on 2026.2+; any other command falls through to the platform default.
 *
 * `LspCommandsSupport.executeCommand(LspServer, VirtualFile, Command)` is the one overload present
 * on both the 2024.3 and 2026.2 platforms (2026.2's `LspClient` overload delegates to it).
 *
 * The platform caches the lenses and moves them with edits, but not their arguments, so a click can carry line
 * numbers from before the last edit. Before running a positional command this asks the server for the file's
 * current lenses and runs the one the clicked lens now corresponds to (see [StaleLensCommandResolver]); when the
 * clicked lens was stale it also asks the platform to recompute its lenses.
 */
class ReqnrollLspCommandsSupport : LspCommandsSupport() {
    override fun executeCommand(server: LspServer, contextFile: VirtualFile, command: Command) {
        val action = parseLensCommand(command)
        if (action == null) {
            super.executeCommand(server, contextFile, command)
            return
        }
        ReqnrollDebugLogger.info("ReqnrollLspCommandsSupport: lens command ${command.command} -> $action")
        val project = server.project
        val uri = action.uri
        if (uri == null) {
            run(project, action)
            return
        }
        // The fresh codeLens request is synchronous, so it must not run on the EDT the click arrives on.
        ApplicationManager.getApplication().executeOnPooledThread {
            val current = currentAction(project, uri, command, action)
            ApplicationManager.getApplication().invokeLater {
                if (project.isDisposed) return@invokeLater
                if (current != null) {
                    run(project, current)
                } else {
                    ReqnrollNotify.info(project, "This lens was out of date and has been refreshed.", "Reqnroll")
                }
            }
        }
    }

    /** The action to run for a click on [command]: itself, the fresh lens it now corresponds to, or null if it is gone. */
    private fun currentAction(project: Project, uri: String, command: Command, action: LensCommand): LensCommand? {
        val fresh = ReqnrollRequestSender.codeLens(project, uri) ?: return action // No answer: keep the click as it was.
        return when (val resolution = StaleLensCommandResolver.resolve(command, fresh)) {
            StaleLensCommandResolver.Resolution.Current -> action
            is StaleLensCommandResolver.Resolution.Rebased -> {
                val rebased = parseLensCommand(resolution.command)
                ReqnrollDebugLogger.info("ReqnrollLspCommandsSupport: stale lens command $action rebased to $rebased")
                NativeLspCodeLens.requestRefresh(project)
                rebased
            }
            StaleLensCommandResolver.Resolution.Gone -> {
                ReqnrollDebugLogger.info("ReqnrollLspCommandsSupport: stale lens command $action has no current lens")
                NativeLspCodeLens.requestRefresh(project)
                null
            }
        }
    }

    private fun run(project: Project, action: LensCommand) {
        when (action) {
            is LensCommand.FindStepUsages -> FindStepUsagesRunner.runAndShow(project, action.uri, action.line, action.character)
            is LensCommand.NoStepUsages -> FindStepUsagesRunner.showNoUsages(project)
            is LensCommand.GoToMatchingScenarios -> GoToMatchingScenariosRunner.runAndShow(project, action.uri, action.line, action.character)
            is LensCommand.GoToHooks -> GoToHooksRunner.runAndShow(
                project, action.uri, action.line, action.character, action.ownLevelOnly,
                alwaysShowPicker = true, source = RiderTelemetryTransmitter.GO_TO_HOOK_SOURCE_CODE_LENS,
            )
        }
    }

    companion object {
        /** Pure, so the argument handling is testable without a platform fixture. Null for any command that isn't one of ours. */
        internal fun parseLensCommand(command: Command): LensCommand? {
            val args = command.arguments
            val uri = args?.getOrNull(0)?.let { raw -> if (raw is JsonPrimitive) raw.asString else raw.toString() }
            val line = HookLensSupport.argAsInt(args, 1)
            val character = HookLensSupport.argAsInt(args, 2)
            return when (command.command) {
                "reqnroll.noStepUsages" -> LensCommand.NoStepUsages
                "reqnroll.findStepUsages" ->
                    if (uri != null && line != null) LensCommand.FindStepUsages(uri, line, character ?: 0) else null
                "reqnroll.goToMatchingScenarios" ->
                    if (uri != null && line != null && character != null) LensCommand.GoToMatchingScenarios(uri, line, character) else null
                "reqnroll.goToHooks" ->
                    if (uri != null && line != null && character != null)
                        LensCommand.GoToHooks(uri, line, character, HookLensSupport.argAsBoolean(args, 3) ?: false)
                    else null
                else -> null
            }
        }
    }
}

/** A server CodeLens command the client executes itself. */
internal sealed interface LensCommand {
    /** The document the command navigates from, or null for a command without a position. */
    val uri: String?

    data class FindStepUsages(override val uri: String, val line: Int, val character: Int) : LensCommand
    data object NoStepUsages : LensCommand {
        override val uri: String? = null
    }
    data class GoToMatchingScenarios(override val uri: String, val line: Int, val character: Int) : LensCommand
    data class GoToHooks(override val uri: String, val line: Int, val character: Int, val ownLevelOnly: Boolean) : LensCommand
}
