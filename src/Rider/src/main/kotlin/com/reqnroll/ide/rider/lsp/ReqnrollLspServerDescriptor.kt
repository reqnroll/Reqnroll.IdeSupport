package com.reqnroll.ide.rider.lsp

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.Lsp4jClient
import com.intellij.platform.lsp.api.LspServerNotificationsHandler
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import com.intellij.platform.lsp.api.customization.LspDiagnosticsSupport
import com.intellij.platform.lsp.api.customization.LspFormattingSupport
import com.intellij.platform.lsp.api.customization.LspSemanticTokensSupport
import com.reqnroll.ide.rider.isCsExtension
import com.reqnroll.ide.rider.isFeatureExtension
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.diagnostics.ReqnrollLspDiagnosticsSupport
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollLanguageServer
import com.reqnroll.ide.rider.lsp.semantictokens.ReqnrollSemanticTokensSupport
import com.reqnroll.ide.rider.telemetry.ReqnrollTelemetryEventInterceptor
import org.eclipse.lsp4j.ClientCapabilities
import org.eclipse.lsp4j.CodeLensWorkspaceCapabilities
import org.eclipse.lsp4j.FormattingCapabilities
import org.eclipse.lsp4j.InlayHintWorkspaceCapabilities
import org.eclipse.lsp4j.OnTypeFormattingCapabilities
import org.eclipse.lsp4j.RangeFormattingCapabilities
import org.eclipse.lsp4j.TextDocumentClientCapabilities
import org.eclipse.lsp4j.WorkspaceClientCapabilities
import org.eclipse.lsp4j.services.LanguageServer

/**
 * Central configuration point for the Reqnroll LSP server as far as Rider's generic
 * `com.intellij.platform.lsp.api` client is concerned: which files it covers, how to launch it,
 * and which optional client-side customizations (go to definition, semantic token coloring, the
 * custom reqnroll-prefixed protocol extensions) to enable. One instance is created per project by
 * [ReqnrollLspServerSupportProvider].
 */
class ReqnrollLspServerDescriptor(project: Project) :
    ProjectWideLspServerDescriptor(project, "Reqnroll") {

    // Unlike semantic tokens/completion/diagnostics (which default to auto-enable based
    // on server capability), the platform defaults Go To Definition support to off —
    // must opt in explicitly. FeatureDefinitionHandler/GoToStepDefinitionsHandler
    // implement it server-side. Hover isn't implemented server-side, so left disabled.
    override val lspGoToDefinitionSupport: Boolean = true

    // Without this, Rider's platform default only colors the LSP *standard* token type
    // vocabulary (confirmed by decompiling LspSemanticTokensSupport.getTextAttributesKey — a
    // fixed switch over ~23 hardcoded names) and silently ignores our custom `reqnroll.*`
    // names — same class of problem VS's built-in colorizer has. See
    // com/reqnroll/ide/rider/lsp/semantictokens/ReqnrollSemanticTokensSupport.kt.
    override val lspSemanticTokensSupport: LspSemanticTokensSupport = ReqnrollSemanticTokensSupport()

    // Diagnostic messages (e.g. the ambiguous-step "Ambiguous steps:\n<candidate1>\n<candidate2>..."
    // text built by MatchResult.CreateMultiMatch) are plain LSP strings — no markdown/HTML per spec.
    // Confirmed by decompiling LspDiagnosticsSupport.getTooltip: the platform default forwards
    // Diagnostic.message verbatim into AnnotationBuilder.tooltip(String), which Rider's hover popup
    // renders as HTML, collapsing bare '\n' the same as any other whitespace run — so multi-candidate
    // messages rendered with the platform default all merge onto one row instead of one candidate per
    // line. See com/reqnroll/ide/rider/lsp/diagnostics/ReqnrollLspDiagnosticsSupport.kt. (#176)
    override val lspDiagnosticsSupport: LspDiagnosticsSupport = ReqnrollLspDiagnosticsSupport()

    // Enables the platform's built-in LspFormattingService (an AsyncDocumentFormattingService) for
    // whole-document Reformat Code. Confirmed by decompiling LspFormattingService.canFormat that
    // eligibility here is PSI-independent — unlike declarative inlay hints, it does NOT require a
    // registered FormattingModelBuilder for .feature's language; it only needs this property
    // non-null and requires !hasFormattingModelBuilder (true for .feature, since none is
    // registered) plus the server advertising documentFormattingProvider (already true via
    // GherkinFormattingHandler). The platform's LspFormattingTask sends the standard
    // textDocument/formatting request and applies the returned edits itself — no client glue
    // needed. Note: LspFormattingService.getFeatures() decompiles to emptySet(), so
    // FormattingService.Feature.FORMAT_FRAGMENTS is not declared — Reformat Selection does not
    // route through this path (whole-document only); on-type table-column realignment likewise
    // has no generic platform hook and would need separate custom client glue.
    override val lspFormattingSupport: LspFormattingSupport = LspFormattingSupport()

    // Adds the reqnroll-prefixed client-to-server notifications (ReqnrollNotificationSender) on top
    // of the standard LanguageServer interface. Confirmed against Rider 2024.3.5's actual
    // bundled LspServerDescriptor.class that this property genuinely exists and is overridable
    // at this platform version — see docs/Rider-Project-Document-Sync-Implementation-Plan.md.
    // Kotlin `open val` in the superclass — must override as `val`, not `fun getXxx()`, even
    // though the compiled superclass's JVM member is a getLsp4jServerClass() method.
    override val lsp4jServerClass: Class<out LanguageServer> = ReqnrollLanguageServer::class.java

    /** This server covers `.feature` files (Gherkin) and `.cs` files (step-definition bindings). */
    override fun isSupportedFile(file: VirtualFile): Boolean =
        isFeatureExtension(file.extension) || isCsExtension(file.extension)

    /**
     * Wraps the platform's own handler so `workspace/inlayHint/refresh` also refreshes `.feature`
     * inlay hints (see [ReqnrollInlayHintRefreshInterceptor]), `workspace/codeLens/refresh` also
     * refreshes the step-usages CodeVision lens (see [ReqnrollCodeLensRefreshInterceptor]), and
     * `telemetry/event` is forwarded to Application Insights (see
     * [ReqnrollTelemetryEventInterceptor]) — nested so all three wrap the same underlying platform
     * handler via Kotlin interface delegation.
     */
    override fun createLsp4jClient(serverNotificationsHandler: LspServerNotificationsHandler): Lsp4jClient =
        Lsp4jClient(
            ReqnrollTelemetryEventInterceptor(
                ReqnrollCodeLensRefreshInterceptor(
                    project,
                    ReqnrollInlayHintRefreshInterceptor(project, serverNotificationsHandler),
                ),
            ),
        )

    // Rider's own default ClientCapabilities doesn't advertise workspace.inlayHint.refreshSupport
    // (confirmed live: the server's InlayHintRefreshHandler never fired — its capability guard
    // saw it as unset — while the equivalent semanticTokens.refreshSupport, which Rider *does*
    // advertise, fired every time). Presumably because Rider's generic client has no built-in
    // consumer for inlay hints to refresh (same "capability-name bookkeeping only" finding noted
    // elsewhere in this file). Since ReqnrollInlayHintRefreshInterceptor gives it a real one now,
    // advertise the capability truthfully rather than leaving the server's push silently gated off.
    // Rider's generic LspFormattingService (see lspFormattingSupport above) only treats a server as
    // "explicitly wanting" to format a file when it finds a *dynamically registered*
    // textDocument/formatting capability whose DocumentSelector matches that file — confirmed by
    // decompiling LspServerImpl's private capability-lookup helper, which reads solely from
    // LspDynamicCapabilities and never consults the static documentFormattingProvider flag from the
    // initialize response (that flag only feeds the separate, already-satisfied
    // hasFormattingRelatedCapabilities check). GherkinFormattingHandler is registered server-side via
    // AddHandler<>() (dynamic registration, per lsp-handler-dynamic-registration precedent), but
    // OmniSharp only attempts it when the client advertises dynamicRegistration=true for the
    // capability — Rider's platform default doesn't. Same gap class as workspace.inlayHint.refreshSupport
    // above; fixed the same way, for all three formatting-related capabilities the server implements
    // (document, range, on-type — see GherkinFormattingHandler).
    override val clientCapabilities: ClientCapabilities
        get() = super.clientCapabilities.apply {
            val workspaceCapabilities = workspace ?: WorkspaceClientCapabilities().also { workspace = it }
            workspaceCapabilities.inlayHint = InlayHintWorkspaceCapabilities(true)
            // Not required for the server to actually send workspace/codeLens/refresh (it does so
            // unconditionally, unlike the capability-gated inlayHint/semanticTokens refreshes —
            // see BindingRegistryChangedHandler.RequestCodeLensRefreshAsync), but advertised
            // truthfully now that ReqnrollCodeLensRefreshInterceptor gives it a real consumer.
            workspaceCapabilities.codeLens = CodeLensWorkspaceCapabilities(true)

            val textDocumentCapabilities = textDocument ?: TextDocumentClientCapabilities().also { textDocument = it }
            textDocumentCapabilities.formatting = FormattingCapabilities(true)
            textDocumentCapabilities.rangeFormatting = RangeFormattingCapabilities(true)
            textDocumentCapabilities.onTypeFormatting = OnTypeFormattingCapabilities(true)
        }

    /** Resolves the bundled server executable and builds the launch command line, with the log level chosen per [resolveLogLevel]. */
    override fun createCommandLine(): GeneralCommandLine {
        val serverPath = try {
            ReqnrollServerPathResolver.resolve()
        } catch (ex: Exception) {
            ReqnrollDebugLogger.error("createCommandLine: failed to resolve LSP server path", ex)
            throw ex
        }

        // "reqnroll.devSandbox" is set by build.gradle.kts only on the JVM that `runIde` launches
        // (a local dev sandbox) — never present in a real installed plugin — so local dev gets
        // full diagnostic logging without needing a manual override every time.
        val logLevel = resolveLogLevel(System.getProperty("reqnroll.devSandbox") == "true")

        ReqnrollDebugLogger.info("createCommandLine: launching $serverPath --ide rider --log-level $logLevel")
        return GeneralCommandLine(serverPath.toString())
            .withParameters("--ide", "rider", "--log-level", logLevel)
    }

    companion object {
        /** Pure/parameterized so it's testable without mutating the real System property — see ReqnrollServerPathResolver's identical rationale. */
        internal fun resolveLogLevel(isDevSandbox: Boolean): String =
            if (isDevSandbox) "Verbose" else "Warning"
    }
}
