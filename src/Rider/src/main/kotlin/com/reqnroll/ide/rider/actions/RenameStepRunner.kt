package com.reqnroll.ide.rider.actions

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollNotificationSender
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.lsp.protocol.RenameTargetItem
import com.reqnroll.ide.rider.lsp.protocol.SelectRenameTargetParams

/**
 * Shared "disambiguate, prompt, rename" logic for [RenameFeatureStepAction]/[RenameCSharpStepAction]
 * — the Rider-side surface for the Step Rename refactoring (issue #160). Mirrors VS's
 * `RenameStepCommand`/VS Code's `renameDisambiguation.ts`: `reqnroll/renameTargets` picks the
 * candidate binding (skipping straight past `textDocument/prepareRename` and seeding the prompt
 * from the target's own `expression` field, exactly like VS's command does), then
 * `reqnroll/selectRenameTarget` records the choice before `textDocument/rename` builds the edit.
 *
 * Rider has no native rename bridge (confirmed by decompiling `LspServerDescriptor`) and the
 * server does not proactively push `workspace/applyEdit` to non-VS clients, so the returned
 * `WorkspaceEdit` is applied locally via [RenameWorkspaceEditApplier].
 */
object RenameStepRunner {
    fun run(project: Project, uri: String, line: Int, character: Int) {
        ReqnrollDebugLogger.info("RenameStepRunner: invoked for $uri at $line:$character")
        // Captured once, up front, so the edit-application step at the end of this flow can
        // detect whether the document changed at any point in between -- including across the
        // modal "Enter the new step expression" dialog, which gives the user arbitrary time to
        // edit the file before the rename request is even sent (issue #326).
        val requestModificationStamp = RenameWorkspaceEditApplier.documentForUri(uri)?.modificationStamp
        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Renaming Step", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.renameTargets(project, uri, line, character)

                if (response == null) {
                    showOnEdt(project) {
                        Messages.showErrorDialog(
                            project, "The Reqnroll LSP server is not running or did not respond.", "Rename Step")
                    }
                    return
                }

                if (response.targets.isEmpty()) {
                    showOnEdt(project) {
                        Messages.showInfoMessage(project, "No renameable step at this position.", "Rename Step")
                    }
                    return
                }

                if (response.targets.size == 1) {
                    continueWithTarget(project, uri, line, character, response.targets[0], requestModificationStamp)
                    return
                }

                // Ambiguous: JBPopupFactory's chooser is non-modal — onChosen fires asynchronously
                // on the EDT once the user picks, so the rest of the flow must resume from there
                // (hopping back to a background thread for the LSP calls) rather than blocking this
                // Task.Backgroundable's thread waiting for a synchronous return.
                ApplicationManager.getApplication().invokeLater {
                    if (project.isDisposed) return@invokeLater
                    ReqnrollResultPopup.show(
                        project,
                        "${response.targets.size} Matching Binding(s)",
                        response.targets,
                        render = { it.label },
                        onChosen = { target ->
                            ApplicationManager.getApplication().executeOnPooledThread {
                                continueWithTarget(project, uri, line, character, target, requestModificationStamp)
                            }
                        },
                    )
                }
            }
        })
    }

    /** Runs on a background thread: records the selection, prompts for the new expression, sends the rename request, and applies the resulting edit. */
    private fun continueWithTarget(
        project: Project,
        uri: String,
        line: Int,
        character: Int,
        target: RenameTargetItem,
        requestModificationStamp: Long?,
    ) {
        ReqnrollNotificationSender.sendSelectRenameTarget(
            project, SelectRenameTargetParams(uri, version = 0, attributeIndex = target.attributeIndex))

        var input: String? = null
        showOnEdt(project) {
            input = Messages.showInputDialog(
                project, "Enter the new step expression:", "Rename Step", null, target.expression, null)
        }
        val newExpression = if (isValidNewExpression(target.expression, input)) input else null
        if (newExpression == null) return

        val edit = ReqnrollRequestSender.rename(project, uri, line, character, newExpression)
        if (edit == null) {
            showOnEdt(project) {
                Messages.showErrorDialog(
                    project, "Rename failed — the new expression may be invalid, or nothing to rename.", "Rename Step")
            }
            return
        }

        ApplicationManager.getApplication().invokeLater {
            if (project.isDisposed) return@invokeLater
            if (RenameWorkspaceEditApplier.documentForUri(uri)?.modificationStamp != requestModificationStamp) {
                ReqnrollDebugLogger.warn(
                    "RenameStepRunner: $uri changed since the rename was requested; discarding the " +
                        "edit to avoid applying it at stale offsets.",
                )
                showOnEdt(project) {
                    Messages.showErrorDialog(
                        project,
                        "The file changed while the rename dialog was open. Please try again.",
                        "Rename Step",
                    )
                }
                return@invokeLater
            }
            RenameWorkspaceEditApplier.apply(project, edit)
        }
    }

    /** True when [input] is a non-blank change from [currentExpression] — i.e. not a cancel/no-op. */
    internal fun isValidNewExpression(currentExpression: String, input: String?): Boolean =
        !input.isNullOrBlank() && input != currentExpression

    private fun showOnEdt(project: Project, block: () -> Unit) {
        ApplicationManager.getApplication().invokeAndWait {
            if (!project.isDisposed) block()
        }
    }
}
