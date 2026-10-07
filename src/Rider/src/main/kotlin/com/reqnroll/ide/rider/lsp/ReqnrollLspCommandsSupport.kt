package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.customization.LspCommandsSupport
import com.reqnroll.ide.rider.actions.FindStepUsagesRunner
import com.reqnroll.ide.rider.actions.GoToHooksRunner
import com.reqnroll.ide.rider.actions.GoToMatchingScenariosRunner
import com.reqnroll.ide.rider.codevision.HookLensSupport
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.telemetry.RiderTelemetryTransmitter
import org.eclipse.lsp4j.Command

/**
 * Runs the server's *client-side* CodeLens commands (`reqnroll.findStepUsages`,
 * `reqnroll.goToHooks`, `reqnroll.goToMatchingScenarios`, `reqnroll.noStepUsages`) when a lens is
 * clicked (#909).
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
 */
class ReqnrollLspCommandsSupport : LspCommandsSupport() {
    override fun executeCommand(server: LspServer, contextFile: VirtualFile, command: Command) {
        val action = parseLensCommand(command)
        if (action == null) {
            super.executeCommand(server, contextFile, command)
            return
        }
        ReqnrollDebugLogger.info("ReqnrollLspCommandsSupport: lens command ${command.command} -> $action")
        run(server.project, action)
    }

    private fun run(project: com.intellij.openapi.project.Project, action: LensCommand) {
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
            val uri = args?.getOrNull(0)?.let { raw -> if (raw is com.google.gson.JsonPrimitive) raw.asString else raw.toString() }
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
    data class FindStepUsages(val uri: String, val line: Int, val character: Int) : LensCommand
    data object NoStepUsages : LensCommand
    data class GoToMatchingScenarios(val uri: String, val line: Int, val character: Int) : LensCommand
    data class GoToHooks(val uri: String, val line: Int, val character: Int, val ownLevelOnly: Boolean) : LensCommand
}
