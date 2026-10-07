package com.reqnroll.ide.rider.navigation

import com.intellij.openapi.keymap.KeymapManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import java.util.concurrent.atomic.AtomicBoolean
import javax.swing.KeyStroke

/**
 * Diagnostic for #909: logs, once per IDE session, what the active keymap actually binds for the Go
 * to Definition shortcuts — which keymap is active, the shortcuts it gives [ReqnrollGoToDefinitionAction]
 * and the platform's `GotoDeclaration`, and which actions it resolves F12 / Ctrl+B to. Remove once
 * the F12 / Ctrl+click routing is understood.
 */
class ReqnrollKeymapDiagnostics : ProjectActivity {
    override suspend fun execute(project: Project) {
        if (!logged.compareAndSet(false, true)) return
        try {
            val keymap = KeymapManager.getInstance().activeKeymap
            ReqnrollDebugLogger.info("keymap: active=${keymap.name} parent=${keymap.parent?.name}")
            for (id in listOf("Reqnroll.GoToDefinition", "GotoDeclaration", "GotoDeclarationOnly")) {
                ReqnrollDebugLogger.info("keymap: $id -> ${keymap.getShortcuts(id).joinToString { it.toString() }}")
            }
            for (stroke in listOf("F12", "control B")) {
                val ids = keymap.getActionIds(KeyStroke.getKeyStroke(stroke)).joinToString()
                ReqnrollDebugLogger.info("keymap: '$stroke' -> [$ids]")
            }
        } catch (e: Exception) {
            ReqnrollDebugLogger.warn("keymap diagnostics failed", e)
        }
    }

    private companion object {
        val logged = AtomicBoolean(false)
    }
}
