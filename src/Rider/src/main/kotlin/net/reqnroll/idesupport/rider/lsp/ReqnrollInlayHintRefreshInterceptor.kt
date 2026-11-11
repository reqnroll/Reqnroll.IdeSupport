package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import net.reqnroll.idesupport.rider.inlayhints.ReqnrollFeatureInlayHintsController
import net.reqnroll.idesupport.rider.taglinks.ReqnrollFeatureTagLinkController
import java.util.concurrent.CompletableFuture

/**
 * Delegates every [LspServerNotificationsHandler] callback straight through to Rider's own
 * platform-provided [handler], except [refreshInlayHints] — there it also refreshes this
 * project's `.feature` inlay hints ([ReqnrollFeatureInlayHintsController]) and tag links
 * ([ReqnrollFeatureTagLinkController]) before delegating.
 *
 * The server already sends the *standard* `workspace/inlayHint/refresh` request — purpose-built
 * for exactly this — debounced, whenever binding discovery changes and the client advertised
 * `workspace.inlayHint.refreshSupport` (see `InlayHintRefreshHandler` server-side). No new
 * `reqnroll`-prefixed protocol message needed. Installed via
 * [ReqnrollLspServerDescriptor.createLsp4jClient].
 *
 * Tag links have no refresh request of their own in the LSP spec, so they piggyback on this same
 * signal rather than getting their own interceptor.
 */
class ReqnrollInlayHintRefreshInterceptor(
    private val project: Project,
    private val handler: LspServerNotificationsHandler,
) : LspServerNotificationsHandler by handler {
    override fun refreshInlayHints(): CompletableFuture<Void> {
        ReqnrollFeatureInlayHintsController.refreshOpenFeatureEditors(project)
        ReqnrollFeatureTagLinkController.refreshOpenFeatureEditors(project)
        return handler.refreshInlayHints()
    }
}
