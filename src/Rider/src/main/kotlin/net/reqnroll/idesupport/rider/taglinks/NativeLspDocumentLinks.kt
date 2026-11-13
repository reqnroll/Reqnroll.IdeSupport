package net.reqnroll.idesupport.rider.taglinks

import net.reqnroll.idesupport.rider.folding.NativeLspFolding

/**
 * Whether the running Rider follows the server's `textDocument/documentLink` links by itself.
 *
 * Rider's generic LSP client requests document links and turns each into a reference (`LspDocumentLinkReferenceProvider`),
 * so Ctrl/Cmd+click on a tag link opens it. Together with [ReqnrollFeatureTagLinkController]'s own click handling,
 * one click opened the link twice (seen on Rider 2026.2.3.1). The platform's switch, `LspDocumentLinkDisabled`, is a
 * descriptor customization this plugin cannot set while it compiles against an older Rider, so the controller lets
 * the platform open the link instead and keeps only the underline, the hover hint and the telemetry.
 *
 * The public customization class `LspDocumentLinkSupport`, which arrived with the feature, is what is looked for.
 */
internal object NativeLspDocumentLinks {
    private const val SUPPORT_CLASS = "com.intellij.platform.lsp.api.customization.LspDocumentLinkSupport"

    val isFollowedByPlatform: Boolean by lazy {
        NativeLspFolding.classExists(SUPPORT_CLASS, NativeLspDocumentLinks::class.java.classLoader)
    }
}
