package net.reqnroll.idesupport.rider.lsp

import com.intellij.execution.process.ProcessHandler
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerListener
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerState
import com.intellij.util.concurrency.AppExecutorUtil
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
import org.eclipse.lsp4j.InitializeResult
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/**
 * The Reqnroll server's lifecycle for one project, as seen through the platform's public
 * [LspServerListener] (handed out by [ReqnrollLspServerDescriptor.lspServerListener]) plus the
 * descriptor's launch hook. Feeds two consumers: [ReqnrollLspServerReadiness] (run something once
 * the server has completed its LSP handshake) and the server-lifecycle telemetry
 * ([ReqnrollServerLifecycleTelemetry]).
 *
 * The platform sets [LspServerState.Running] before it calls [serverInitialized], so a subscriber
 * is never notified before the server is ready to receive notifications. Callbacks arrive on
 * whatever platform thread completes the transition, not the EDT.
 */
@Service(Service.Level.PROJECT)
class ReqnrollServerEvents(private val project: Project) : LspServerListener {
    private val runningSubscribers = CopyOnWriteArrayList<() -> Unit>()
    private val processTracker = ReqnrollServerProcessTracker()
    private val restartPolicy = ServerRestartPolicy()
    private val telemetry = ReqnrollServerLifecycleTelemetry(
        RiderTelemetryTransmitter::transmit,
        processTracker::startDetails,
        // Waits for the process to finish terminating, so never on the thread that reported the stop.
        { onReady ->
            // Bound now, while the run that stopped is still the current one.
            val describeExit = processTracker.exitDescriber()
            ApplicationManager.getApplication().executeOnPooledThread {
                try {
                    onReady(describeExit(ReqnrollServerProcessTracker.EXIT_DETAILS_TIMEOUT_MS))
                } catch (e: Exception) {
                    ReqnrollDebugLogger.error("ReqnrollServerEvents: reporting the server exit failed", e)
                }
            }
        },
    )

    /** The `--server-session-id` for the server being launched; set by [serverStarting]. */
    val serverSessionId: String?
        get() = processTracker.sessionId

    /**
     * Whether the platform currently has the Reqnroll server in [LspServerState.Running]. Read from the
     * platform on every call: a restart replaces the server (via `fileOpened`) before the new process
     * is launched, and the old server's stop is not reported before then, so any state tracked from
     * the listener callbacks alone is stale exactly when a restart's readiness check runs.
     */
    val isRunning: Boolean
        get() = LspServerManager.getInstance(project)
            .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)
            .firstOrNull()?.state == LspServerState.Running

    /** The server process is about to be launched (first start, or any restart): gives it a fresh session id. */
    fun serverStarting() {
        processTracker.onStarting()
        telemetry.onStateChanged(LspServerState.Initializing)
    }

    /** The server process was created; its stderr and exit feed the exit telemetry. */
    fun serverProcessStarted(handler: ProcessHandler) {
        processTracker.onProcessStarted(handler)
    }

    /** Launching the server process threw, so the platform will not report a stop. */
    fun serverStartFailed() {
        telemetry.onStateChanged(LspServerState.ShutdownUnexpectedly)
    }

    override fun serverInitialized(params: InitializeResult) {
        telemetry.onStateChanged(LspServerState.Running)
        // One failing subscriber must not starve the others, nor propagate into the platform's handshake.
        runningSubscribers.forEach {
            try {
                it()
            } catch (e: Exception) {
                ReqnrollDebugLogger.error("ReqnrollServerEvents: a server-running subscriber failed", e)
            }
        }
    }

    override fun serverStopped(shutdownNormally: Boolean) {
        telemetry.onStateChanged(
            if (shutdownNormally) LspServerState.ShutdownNormally else LspServerState.ShutdownUnexpectedly,
        )
        if (!shutdownNormally) scheduleRestart()
    }

    /**
     * The platform leaves a crashed server registered in [LspServerState.ShutdownUnexpectedly], so it
     * never starts a replacement on its own. Restarts are capped by [restartPolicy]; past the cap the
     * server stays stopped until the user restarts it.
     */
    private fun scheduleRestart() {
        if (!restartPolicy.tryAcquire()) {
            ReqnrollDebugLogger.warn("ReqnrollServerEvents: server crashed again too soon; not restarting automatically")
            return
        }
        AppExecutorUtil.getAppScheduledExecutorService().schedule({
            try {
                if (!project.isDisposed) {
                    LspServerManager.getInstance(project).stopAndRestartIfNeeded(ReqnrollLspServerSupportProvider::class.java)
                }
            } catch (e: Exception) {
                ReqnrollDebugLogger.error("ReqnrollServerEvents: automatic server restart failed", e)
            }
        }, RESTART_DELAY_MS, TimeUnit.MILLISECONDS)
    }

    /** Calls [onRunning] after every transition to running until the returned handle is invoked. */
    fun subscribeRunning(onRunning: () -> Unit): () -> Unit {
        // Wrapped so removal is by this registration, not by lambda equality.
        val registration: () -> Unit = { onRunning() }
        runningSubscribers.add(registration)
        return { runningSubscribers.remove(registration) }
    }

    private companion object {
        const val RESTART_DELAY_MS = 1000L
    }
}
