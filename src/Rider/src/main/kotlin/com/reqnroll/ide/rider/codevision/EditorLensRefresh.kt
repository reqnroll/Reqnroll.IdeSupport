package com.reqnroll.ide.rider.codevision

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
 * carried its own copy of the identical `EditorFactory`/`FileDocumentManager` walk (issue #262
 * follow-up).
 */
object EditorLensRefresh {
    /** Invalidates [providerIds] in every open editor in [project] whose file has [extension] (case-insensitive, no leading dot). */
    fun invalidate(project: Project, extension: String, providerIds: List<String>) {
        val codeVisionHost = project.service<CodeVisionHost>()
        for (editor in EditorFactory.getInstance().allEditors) {
            if (editor.project != project) continue
            val virtualFile = FileDocumentManager.getInstance().getFile(editor.document) ?: continue
            if (!virtualFile.extension.equals(extension, ignoreCase = true)) continue
            codeVisionHost.invalidateProvider(CodeVisionHost.LensInvalidateSignal(editor, providerIds))
        }
    }
}
