package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import net.reqnroll.idesupport.rider.breadcrumbs.ReqnrollFeatureBreadcrumbsCollector
import net.reqnroll.idesupport.rider.folding.ReqnrollFeatureFoldingController
import net.reqnroll.idesupport.rider.inlayhints.ReqnrollFeatureInlayHintsController
import net.reqnroll.idesupport.rider.taglinks.ReqnrollFeatureTagLinkController
import java.util.concurrent.CompletableFuture

/**
 * Delegates every [LspServerNotificationsHandler] callback straight through to Rider's own
 * platform-provided [handler], except [refreshInlayHints] — there it also refreshes this
 * project's `.feature` inlay hints ([ReqnrollFeatureInlayHintsController]), folding ranges
 * ([ReqnrollFeatureFoldingController]) and breadcrumbs
 * ([ReqnrollFeatureBreadcrumbsCollector]) before delegating.
 *
 * The server already sends the *standard* `workspace/inlayHint/refresh` request — purpose-built
 * for exactly this — debounced, whenever binding discovery changes and the client advertised
 * `workspace.inlayHint.refreshSupport` (see `InlayHintRefreshHandler` server-side). No new
 * `reqnroll`-prefixed protocol message needed. Installed via
 * [ReqnrollLspServerDescriptor.createLsp4jClient].
 *
 * Folding and breadcrumbs have no `workspace/foldingRange/refresh` or equivalent
 * `documentSymbol` refresh request in the LSP spec, so both piggyback on this same signal
 * rather than getting their own interceptor — see [ReqnrollFeatureFoldingController]'s
 * and [ReqnrollFeatureBreadcrumbsCollector]'s class docs for why each needs one at all.
 */
class ReqnrollInlayHintRefreshInterceptor(
    private val project: Project,
    private val handler: LspServerNotificationsHandler,
) : LspServerNotificationsHandler by handler {
    override fun refreshInlayHints(): CompletableFuture<Void> {
        ReqnrollFeatureInlayHintsController.refreshOpenFeatureEditors(project)
        ReqnrollFeatureFoldingController.refreshOpenFeatureEditors(project)
        ReqnrollFeatureTagLinkController.refreshOpenFeatureEditors(project)
        ReqnrollFeatureBreadcrumbsCollector.refreshOpenFeatureEditors(project)
        return handler.refreshInlayHints()
    }
}
