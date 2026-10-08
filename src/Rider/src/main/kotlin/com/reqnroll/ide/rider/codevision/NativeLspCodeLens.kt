package com.reqnroll.ide.rider.codevision

import com.intellij.openapi.extensions.ExtensionPointName

/**
 * Whether the running Rider renders `textDocument/codeLens` natively (#909).
 *
 * Rider 2026.2 added a generic CodeVision provider for LSP code lenses
 * (`com.intellij.platform.lsp.impl.features.codeLens.LspCodeVisionProvider`); earlier versions
 * had none, which is why this plugin renders the step-usage lenses itself. On 2026.2+ both would
 * show, so [StepUsagesCodeVisionProvider] stands down there and leaves the lens — whose clicks
 * [com.reqnroll.ide.rider.lsp.ReqnrollLspCommandsSupport] now handles — to the platform.
 */
internal object NativeLspCodeLens {
    private const val PROVIDER_CLASS = "com.intellij.platform.lsp.impl.features.codeLens.LspCodeVisionProvider"

    val isRenderedByPlatform: Boolean by lazy {
        try {
            ExtensionPointName.create<Any>("com.intellij.codeInsight.codeVisionProvider")
                .extensionList.any { it.javaClass.name == PROVIDER_CLASS }
        } catch (e: Exception) {
            false
        }
    }
}
