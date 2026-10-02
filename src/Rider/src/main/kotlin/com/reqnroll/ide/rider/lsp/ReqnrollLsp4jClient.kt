package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.Lsp4jClient
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollEmptyParams
import com.reqnroll.ide.rider.telemetry.ReqnrollTelemetryEventInterceptor
import com.reqnroll.ide.rider.testrunner.RunTestCodeVisionProvider
import org.eclipse.lsp4j.jsonrpc.services.JsonNotification

/**
 * Subclass of the platform's [Lsp4jClient], per that class's own documented extension point ("To
 * handle custom undocumented requests/notifications from the server, plugins need to override
 * [com.intellij.platform.lsp.api.LspClientDescriptor.createLsp4jClient] and return their subclass
 * of this Lsp4jClient... annotated functions... called via reflection by the lsp4j library" —
 * confirmed against the actual platform source, `platform/lsp/src/api/Lsp4jClient.kt` in
 * intellij-community, not assumed): adds the one reqnroll-custom server-to-client push this plugin
 * needs, `reqnroll/testOutcomes/changed` (LSP-server outcome pipeline, #700/#702).
 *
 * A decorator of [LspServerNotificationsHandler] — the approach [ReqnrollCodeLensRefreshInterceptor]
 * and [ReqnrollInlayHintRefreshInterceptor] use for the *standard* LSP pushes — cannot add this: every
 * [Lsp4jClient] method for the standard set is `final override`, and `reqnroll/testOutcomes/changed`
 * has no standard-set counterpart to decorate in the first place. Only a genuine subclass, with its
 * own `@JsonNotification`-annotated method, gives lsp4j something to dispatch to.
 *
 * Without this, every debounced batch of test results the server pushes (the only signal this
 * plugin gets that an outcome changed outside of its own just-triggered run — see
 * `TestOutcomeTcpListener.cs`'s remarks) logged `Unsupported notification method:
 * reqnroll/testOutcomes/changed` from lsp4j's `GenericEndpoint` and did nothing; confirmed against
 * lsp4j's own source that this is a log-and-drop, not a thrown exception, so it degraded silently
 * to "the Run lens only updates from this plugin's own polling," never actually breaking a run.
 *
 * Standard-set pushes still go through the existing decorator chain, now wrapped one layer deeper by
 * this class's own superclass call — installed via [ReqnrollLspServerDescriptor.createLsp4jClient].
 */
class ReqnrollLsp4jClient(
    private val project: Project,
    handler: LspServerNotificationsHandler,
) : Lsp4jClient(
    ReqnrollTelemetryEventInterceptor(
        ReqnrollCodeLensRefreshInterceptor(project, ReqnrollInlayHintRefreshInterceptor(project, handler)),
    ),
) {
    @JsonNotification("reqnroll/testOutcomes/changed")
    fun testOutcomesChanged(@Suppress("UNUSED_PARAMETER") params: ReqnrollEmptyParams) {
        ApplicationManager.getApplication().invokeLater(
            { if (!project.isDisposed) RunTestCodeVisionProvider.refreshOpenFeatureEditors(project) },
            ModalityState.any(),
        )
    }
}
