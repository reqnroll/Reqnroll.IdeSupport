package net.reqnroll.idesupport.rider.codevision

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.extensions.ExtensionPointName
import com.intellij.openapi.project.Project

/**
 * Whether the running Rider renders `textDocument/codeLens` natively.
 *
 * Rider 2026.2 added a generic CodeVision provider for LSP code lenses
 * (`com.intellij.platform.lsp.impl.features.codeLens.LspCodeVisionProvider`); earlier versions
 * had none, which is why this plugin renders the step-usage lenses itself. On 2026.2+ both would
 * show, so [StepUsagesCodeVisionProvider] stands down there and leaves the lens — whose clicks
 * [net.reqnroll.idesupport.rider.lsp.ReqnrollLspCommandsSupport] now handles — to the platform.
 */
internal object NativeLspCodeLens {
    private const val PROVIDER_CLASS = "com.intellij.platform.lsp.impl.features.codeLens.LspCodeVisionProvider"

    /** The platform provider's CodeVision id (its `getId()`), used to ask CodeVision to recompute it. */
    const val PLATFORM_PROVIDER_ID = "LspCodeVisionProvider"

    val isRenderedByPlatform: Boolean by lazy {
        try {
            ExtensionPointName.create<Any>("com.intellij.codeInsight.codeVisionProvider")
                .extensionList.any { it.javaClass.name == PROVIDER_CLASS }
        } catch (e: Exception) {
            false
        }
    }

    /**
     * Asks CodeVision to recompute every Reqnroll lens in the project's open `.feature` and `.cs` editors, which on
     * builds with native lenses includes the platform's provider (see [EditorLensRefresh.invalidate]). Safe to call
     * from any thread.
     */
    fun requestRefresh(project: Project) {
        ApplicationManager.getApplication().invokeLater(
            {
                if (!project.isDisposed) {
                    HookCodeVisionProvider.refreshOpenFeatureEditors(project)
                    StepUsagesCodeVisionProvider.refreshOpenCsEditors(project)
                }
            },
            ModalityState.any(),
        )
    }
}
