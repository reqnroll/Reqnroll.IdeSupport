package net.reqnroll.idesupport.rider.debugger

import com.intellij.ide.plugins.DynamicPluginListener
import com.intellij.ide.plugins.IdeaPluginDescriptor
import com.intellij.openapi.application.EDT
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Puts [StepIntoStepDefinitionAction] in place of the registered Step Into when the first project opens. The action
 * manager has finished its start-up customisation by then, so Rider's own Step Into is the one that gets wrapped;
 * later projects find it already wrapped. Runs on the EDT, since replacing the action updates the toolbar and menu
 * groups that show it.
 */
class StepIntoStepDefinitionInstaller : ProjectActivity {
    override suspend fun execute(project: Project) {
        withContext(Dispatchers.EDT) { StepIntoStepDefinitionAction.install() }
    }
}

/** Restores Rider's own Step Into before this plugin is unloaded without a restart. */
class StepIntoStepDefinitionUnloadListener : DynamicPluginListener {
    override fun beforePluginUnload(pluginDescriptor: IdeaPluginDescriptor, isUpdate: Boolean) {
        if (pluginDescriptor.pluginId.idString == PLUGIN_ID) StepIntoStepDefinitionAction.uninstall()
    }

    private companion object {
        /** This plugin's ID, as `plugin.xml` declares it. */
        const val PLUGIN_ID = "net.reqnroll.idesupport"
    }
}
