package com.reqnroll.ide.rider.actions

import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.vfs.LocalFileSystem
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.lspUriToLocalPath
import javax.swing.BorderFactory
import javax.swing.JLabel

/**
 * The Rider-side equivalent of VS Code's `QuickPick` results list (see
 * `src/VSCode/src/stepUsages.ts`'s `doFindStepUsages`/`doFindUnusedStepDefinitions`): a simple
 * chooser popup listing results, navigating to the picked item's location on selection.
 */
object ReqnrollResultPopup {
    /** Shows a chooser popup of [items]; [render] supplies each row's display text; [onChosen] navigates on selection. */
    fun <T> show(project: Project, title: String, items: List<T>, render: (T) -> String, onChosen: (T) -> Unit) {
        JBPopupFactory.getInstance()
            .createPopupChooserBuilder(items)
            .setTitle(title)
            .setRenderer { _, value, _, _, _ ->
                JLabel(render(value)).apply { border = BorderFactory.createEmptyBorder(2, 8, 2, 8) }
            }
            .setItemChosenCallback { onChosen(it) }
            .createPopup()
            .showCenteredInCurrentWindow(project)
    }

    /** Navigates to a location by absolute file-system path (used for step-definition source locations). */
    fun navigateToPath(project: Project, filePath: String?, line: Int, column: Int) {
        if (filePath.isNullOrBlank()) return
        val file = LocalFileSystem.getInstance().refreshAndFindFileByPath(filePath)
        if (file == null) {
            ReqnrollDebugLogger.warn("ReqnrollResultPopup: could not resolve file $filePath")
            return
        }
        OpenFileDescriptor(project, file, line, column).navigate(true)
    }

    /** Navigates to a location by LSP document URI (used for feature-file step-usage locations). */
    fun navigateToUri(project: Project, uri: String, line: Int, column: Int) {
        val path = lspUriToLocalPath(uri)
        if (path == null) {
            ReqnrollDebugLogger.warn("ReqnrollResultPopup: could not resolve uri $uri")
            return
        }
        navigateToPath(project, path, line, column)
    }
}
