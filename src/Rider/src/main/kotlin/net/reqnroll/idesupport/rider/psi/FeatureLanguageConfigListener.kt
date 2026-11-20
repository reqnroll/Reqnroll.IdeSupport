package net.reqnroll.idesupport.rider.psi

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.openapi.vfs.newvfs.events.VFileMoveEvent
import com.intellij.openapi.vfs.newvfs.events.VFilePropertyChangeEvent
import com.intellij.util.FileContentUtil
import net.reqnroll.idesupport.rider.isFeatureExtension

/**
 * Re-parses the open feature files of a project when its `reqnroll.json` or `specflow.json` is created, edited,
 * renamed, moved or deleted, because the default dialect is read from that file and decides how a file without a
 * `# language:` header is lexed and highlighted. Files that are not open pick the new setting up when they are next
 * parsed.
 */
class FeatureLanguageConfigListener : BulkFileListener {
    override fun after(events: List<VFileEvent>) {
        val directories = events.flatMap(::pathsOf).mapNotNull(FeatureDefaultLanguages::configDirectoryOf).toSet()
        if (directories.isEmpty()) return
        ApplicationManager.getApplication().invokeLater {
            for (project in ProjectManager.getInstance().openProjects) {
                reparseOpenFeatureFiles(project, directories)
            }
        }
    }

    private fun pathsOf(event: VFileEvent): List<String> = when (event) {
        is VFileMoveEvent -> listOf(event.path, event.oldPath)
        is VFilePropertyChangeEvent ->
            if (event.propertyName == VirtualFile.PROP_NAME) listOf(event.path, event.oldPath) else listOf(event.path)
        else -> listOf(event.path)
    }

    private fun reparseOpenFeatureFiles(project: Project, directories: Set<String>) {
        if (project.isDisposed) return
        val files: List<VirtualFile> = FileEditorManager.getInstance(project).openFiles.filter { file ->
            isFeatureExtension(file.extension) && directories.any { FeatureDefaultLanguages.isWithin(it, file.path) }
        }
        if (files.isNotEmpty()) FileContentUtil.reparseFiles(project, files, true)
    }
}
