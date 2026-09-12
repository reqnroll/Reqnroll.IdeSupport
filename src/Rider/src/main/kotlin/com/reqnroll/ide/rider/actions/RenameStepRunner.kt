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
import com.reqnroll.ide.rider.lsp.RenameOutcome
import com.reqnroll.ide.rider.lsp.isDocumentStale
import com.reqnroll.ide.rider.lsp.protocol.RenameAppliedParams
import com.reqnroll.ide.rider.lsp.protocol.RenameTargetItem
import com.reqnroll.ide.rider.lsp.protocol.SelectRenameTargetParams
import org.eclipse.lsp4j.Position

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
        ProgressManager.getInstance().run(object : Task.Backgroundable(
            project, "Reqnroll: Renaming Step", true) {
            override fun run(indicator: ProgressIndicator) {
                val response = ReqnrollRequestSender.renameTargets(project, uri, line, character)

                if (response == null) {
                    showOnEdt(project) {
                        ReqnrollNotify.error(
                            project, "The Reqnroll LSP server is not running or did not respond.", "Rename Step")
                    }
                    return
                }

                if (response.targets.isEmpty()) {
                    showOnEdt(project) {
                        ReqnrollNotify.info(project, "No renameable step at this position.", "Rename Step")
                    }
                    return
                }

                if (response.targets.size == 1) {
                    continueWithTarget(project, uri, line, character, response.targets[0])
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
                                continueWithTarget(project, uri, line, character, target)
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
    ) {
        ReqnrollNotificationSender.sendSelectRenameTarget(
            project,
            SelectRenameTargetParams(
                uri,
                version = 0,
                attributeIndex = target.attributeIndex,
                position = Position(line, character),
            ),
        )

        var input: String? = null
        showOnEdt(project) {
            input = Messages.showInputDialog(
                project, "Enter the new step expression:", "Rename Step", null, target.expression, null)
        }
        val newExpression = if (isValidNewExpression(target.expression, input)) input else null
        if (newExpression == null) return

        // Captured here, immediately before the request — NOT when the action was invoked
        // (issue #671, R4; originally #326).
        //
        // The server computes the edit's offsets fresh inside `textDocument/rename`, from its own
        // current view of the document, and that request is dispatched on its Serial lane, so any
        // didChange the user made while the modal was open has already been applied to the state
        // those offsets came from. The window where the document can still shift out from under
        // them is therefore only the request's own round trip — not the arbitrarily long stretch
        // the modal dialog was open for.
        //
        // Spanning the modal made this fire on changes that could not invalidate the edit, and
        // (via a stamp bumped by a VFS reload rather than a real edit) on changes that had not
        // happened at all — discarding the user's rename and leaving the server's registry
        // describing a step present in no file, since the discard went unreported (issue #670).
        val requestModificationStamp =
            RenameWorkspaceEditApplier.documentForUriWithoutRefresh(uri)?.modificationStamp

        val outcome = ReqnrollRequestSender.rename(project, uri, line, character, newExpression)
        val edit = when (outcome) {
            is RenameOutcome.Failed -> {
                showOnEdt(project) {
                    ReqnrollNotify.error(project, outcome.message, "Rename Step")
                }
                return
            }
            is RenameOutcome.Success -> outcome.edit
        }

        ApplicationManager.getApplication().invokeLater {
            if (project.isDisposed) return@invokeLater
            if (isDocumentStale(
                    RenameWorkspaceEditApplier.documentForUriWithoutRefresh(uri)?.modificationStamp,
                    requestModificationStamp,
                )
            ) {
                ReqnrollDebugLogger.warn(
                    "RenameStepRunner: $uri changed while the rename request was in flight; " +
                        "discarding the edit to avoid applying it at stale offsets.",
                )
                // The server has staged the binding-registry/match-cache updates this edit implies
                // and is waiting to hear whether we applied it. Reporting the discard drops them;
                // staying silent is what used to leave the registry describing a step expression
                // present in no file — "0 step usages" plus unbound-step diagnostics surviving a
                // rebuild and a close/reopen (issue #670).
                reportRenameApplied(project, uri, applied = false)
                showOnEdt(project) {
                    ReqnrollNotify.error(
                        project,
                        "The file changed while the rename was being prepared. Please try again.",
                        "Rename Step",
                    )
                }
                return@invokeLater
            }
            RenameWorkspaceEditApplier.apply(project, edit)
            reportRenameApplied(project, uri, applied = true)
        }
    }

    /**
     * Tells the server whether the returned edit was applied, so it can commit or drop the cache
     * updates it staged (issue #671 R3).
     *
     * Dispatched off the EDT because both call sites sit inside the `invokeLater` block that owns
     * the document write — matching every other notification in this flow, which is sent from a
     * pooled thread. Nothing waits on the result, so there is no ordering requirement against the
     * write command itself.
     */
    private fun reportRenameApplied(project: Project, uri: String, applied: Boolean) {
        ApplicationManager.getApplication().executeOnPooledThread {
            if (project.isDisposed) return@executeOnPooledThread
            ReqnrollDebugLogger.verbose("RenameStepRunner: reporting renameApplied=$applied for $uri")
            ReqnrollNotificationSender.sendRenameApplied(project, RenameAppliedParams(uri, applied))
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
