package com.reqnroll.ide.rider.actions

import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.ui.ColoredListCellRenderer
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.lspUriToLocalPath
import javax.swing.BorderFactory
import javax.swing.JList
import javax.swing.ListCellRenderer

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
            .setRenderer(resultListCellRenderer(render))
            .setItemChosenCallback { onChosen(it) }
            .createPopup()
            .showCenteredInCurrentWindow(project)
    }

    /**
     * Builds the chooser list's cell renderer. A [ColoredListCellRenderer] (rather than the bare,
     * non-opaque `JLabel` used previously) is required so each row derives its background and
     * foreground from the owning list's selection state; without it, keyboard navigation of the
     * Find Usages / Go To Hooks popup has no visible selection highlight (issue #992). Exposed for
     * unit testing, since `show` itself needs a live popup/platform.
     */
    fun <T> resultListCellRenderer(render: (T) -> String): ListCellRenderer<in T> =
        object : ColoredListCellRenderer<T>() {
            override fun customizeCellRenderer(
                list: JList<out T>,
                value: T,
                index: Int,
                selected: Boolean,
                hasFocus: Boolean,
            ) {
                append(render(value))
                border = BorderFactory.createEmptyBorder(2, 8, 2, 8)
            }
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
