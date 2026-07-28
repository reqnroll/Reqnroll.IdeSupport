package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import com.reqnroll.ide.rider.codevision.HookCodeVisionProvider
import com.reqnroll.ide.rider.codevision.StepUsagesCodeVisionProvider
import java.util.concurrent.CompletableFuture

/**
 * Delegates every [LspServerNotificationsHandler] callback straight through to Rider's own
 * platform-provided [handler], except [refreshCodeLenses] — there it also refreshes this
 * project's "N step usages" CodeVision lens ([StepUsagesCodeVisionProvider]) before delegating.
 *
 * Rider's CodeVision engine has no signal of its own for "the data behind this lens changed" —
 * unlike inlay hints/semantic tokens, which at least have *a* refresh mechanism once wired (see
 * [ReqnrollInlayHintRefreshInterceptor]), a stale-until-you-edit-the-.cs-file lens count was the
 * actual reported bug this fixes. Installed via [ReqnrollLspServerDescriptor.createLsp4jClient].
 *
 * [refreshCodeLenses] is invoked directly by `Lsp4jClient.refreshCodeLenses` on whatever thread
 * the underlying `workspace/codeLens/refresh` LSP message arrived on — confirmed live to be a
 * background "LSP Listener" thread, not the EDT. `StepUsagesCodeVisionProvider.refreshOpenCsEditors`
 * calls `CodeVisionHost.invalidateProvider` directly, which asserts EDT-only access (issue #166),
 * unlike the sibling folding/inlay-hint refresh paths ([ReqnrollInlayHintRefreshInterceptor]),
 * which dispatch onto a background thread via `executeOnPooledThread` before ever touching an
 * EDT-sensitive API and only return to the EDT to render. Here there's no LSP request to justify
 * that pooled-thread hop — the fix is simply to defer the CodeVision refresh itself onto the EDT.
 */
class ReqnrollCodeLensRefreshInterceptor(
    private val project: Project,
    private val handler: LspServerNotificationsHandler,
) : LspServerNotificationsHandler by handler {
    override fun refreshCodeLenses(): CompletableFuture<Void> {
        ApplicationManager.getApplication().invokeLater(
            {
                if (!project.isDisposed) {
                    StepUsagesCodeVisionProvider.refreshOpenCsEditors(project)
                    HookCodeVisionProvider.refreshOpenFeatureEditors(project)
                }
            },
            ModalityState.any(),
        )
        return handler.refreshCodeLenses()
    }
}
