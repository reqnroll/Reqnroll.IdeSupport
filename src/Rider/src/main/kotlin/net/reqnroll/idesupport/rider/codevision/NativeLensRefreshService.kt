package net.reqnroll.idesupport.rider.codevision

import com.intellij.openapi.Disposable
import com.intellij.openapi.components.Service
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiManager
import com.intellij.util.Alarm
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger

/**
 * Makes the platform's native lenses ([NativeLspCodeLens]) follow `workspace/codeLens/refresh`.
 *
 * Rider 2026.2 caches each file's `textDocument/codeLens` response and asks the server again only when the project's
 * PSI modification count has changed since the last request. Its handler for `workspace/codeLens/refresh` just
 * recomputes the CodeVision provider and, unlike the inlay hint refresh, does not clear that cache (both checked in
 * the 2026.2.3.1 binaries). So a refresh that is not accompanied by an edit changes nothing: lenses requested before
 * the server had discovered the bindings stay empty (no hook lenses on the first file opened), and lenses whose
 * command arguments predate the last edit stay stale.
 *
 * [request] coalesces a burst of refreshes, then increments the PSI modification count with
 * [PsiManager.dropPsiCaches] (public API; it also drops resolve caches, which is why it is debounced) and asks
 * CodeVision to recompute the open editors' lenses, so the platform's next look at its cache sends a fresh request.
 */
@Service(Service.Level.PROJECT)
internal class NativeLensRefreshService(private val project: Project) : Disposable {
    private val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, this)

    fun request() {
        alarm.cancelAllRequests()
        alarm.addRequest(::refreshNow, DEBOUNCE_MS)
    }

    private fun refreshNow() {
        if (project.isDisposed) return
        ReqnrollDebugLogger.verbose("NativeLensRefreshService: refreshing native lenses")
        PsiManager.getInstance(project).dropPsiCaches()
        HookCodeVisionProvider.refreshOpenFeatureEditors(project)
        StepUsagesCodeVisionProvider.refreshOpenCsEditors(project)
    }

    override fun dispose() = Unit

    private companion object {
        /** Long enough to merge the server's refresh bursts (several within a second at startup). */
        const val DEBOUNCE_MS = 300
    }
}
