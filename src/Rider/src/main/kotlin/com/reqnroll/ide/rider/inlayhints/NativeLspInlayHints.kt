package com.reqnroll.ide.rider.inlayhints

import com.intellij.openapi.extensions.ExtensionPointName

/**
 * Whether the running Rider renders `textDocument/inlayHint` natively (#909).
 *
 * Rider 2026.2 registers a generic inlay-hints provider for LSP servers
 * (`com.intellij.platform.lsp.impl.features.inlayCommon.LspInlayHintsProviderFactory`) that — unlike
 * the declarative framework this plugin originally had to bypass — does show hints in `.feature`
 * files. With both active every hint appeared twice (a truncated pill from
 * [ReqnrollFeatureInlayHintsController] and the full text from the platform), so the controller stands
 * down there; the platform's hints are switched in Settings > Editor > Inlay Hints instead of the
 * Tools-menu toggle.
 */
internal object NativeLspInlayHints {
    private const val PROVIDER_FACTORY_CLASS = "com.intellij.platform.lsp.impl.features.inlayCommon.LspInlayHintsProviderFactory"

    val isRenderedByPlatform: Boolean by lazy {
        try {
            ExtensionPointName.create<Any>("com.intellij.codeInsight.inlayProviderFactory")
                .extensionList.any { it.javaClass.name == PROVIDER_FACTORY_CLASS }
        } catch (e: Exception) {
            false
        }
    }
}
