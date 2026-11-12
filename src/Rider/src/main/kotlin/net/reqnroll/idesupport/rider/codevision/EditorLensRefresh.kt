package net.reqnroll.idesupport.rider.codevision

import com.intellij.codeInsight.codeVision.CodeVisionHost
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project

/**
 * Shared "invalidate this lens in every open editor of a given extension" loop — every
 * `CodeVisionProvider` in this plugin that reacts to `workspace/codeLens/refresh` needs it
 * ([HookCodeVisionProvider.refreshOpenFeatureEditors], [StepUsagesCodeVisionProvider.refreshOpenCsEditors],
 * `RunTestCodeVisionProvider.refreshOpenFeatureEditors`), and until this extraction each one
 * carried its own copy of the identical `EditorFactory`/`FileDocumentManager` walk.
 */
object EditorLensRefresh {
    /**
     * Invalidates [providerIds] in every open editor in [project] whose file has [extension] (case-insensitive, no
     * leading dot).
     *
     * Where the platform renders the server's lenses itself ([NativeLspCodeLens]), its provider is invalidated in the
     * same signal. CodeVision runs one lens calculation per editor at a time and a new one cancels the one in
     * progress, so a signal naming only this plugin's providers could cancel the platform's own recompute after
     * `workspace/codeLens/refresh`, leaving its lenses, and the line numbers their clicks carry, from before the last
     * edit.
     */
    fun invalidate(project: Project, extension: String, providerIds: List<String>) {
        val ids = withPlatformProvider(providerIds, NativeLspCodeLens.isRenderedByPlatform)
        val codeVisionHost = project.service<CodeVisionHost>()
        for (editor in EditorFactory.getInstance().allEditors) {
            if (editor.project != project) continue
            val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: continue
            if (!virtualFile.extension.equals(extension, ignoreCase = true)) continue
            codeVisionHost.invalidateProvider(CodeVisionHost.LensInvalidateSignal(editor, ids))
        }
    }

    /** [providerIds] plus the platform's lens provider when [nativeLenses] is set; pure for testing. */
    internal fun withPlatformProvider(providerIds: List<String>, nativeLenses: Boolean): List<String> =
        if (nativeLenses) (providerIds + NativeLspCodeLens.PLATFORM_PROVIDER_ID).distinct() else providerIds
}
