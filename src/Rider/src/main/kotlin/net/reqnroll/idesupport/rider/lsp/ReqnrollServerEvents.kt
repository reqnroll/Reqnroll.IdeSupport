package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.components.Service
import com.intellij.platform.lsp.api.LspServerListener
import com.intellij.platform.lsp.api.LspServerState
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.telemetry.RiderTelemetryTransmitter
import org.eclipse.lsp4j.InitializeResult
import java.util.concurrent.CopyOnWriteArrayList

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
class ReqnrollServerEvents : LspServerListener {
    private val runningSubscribers = CopyOnWriteArrayList<() -> Unit>()
    private val telemetry = ReqnrollServerLifecycleTelemetry(RiderTelemetryTransmitter::transmit)

    @Volatile
    var isRunning: Boolean = false
        private set

    /** The server process is about to be launched (first start, or any restart). */
    fun serverStarting() {
        isRunning = false
        telemetry.onStateChanged(LspServerState.Initializing)
    }

    override fun serverInitialized(params: InitializeResult) {
        isRunning = true
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
        isRunning = false
        telemetry.onStateChanged(
            if (shutdownNormally) LspServerState.ShutdownNormally else LspServerState.ShutdownUnexpectedly,
        )
    }

    /** Calls [onRunning] after every transition to running until the returned handle is invoked. */
    fun subscribeRunning(onRunning: () -> Unit): () -> Unit {
        // Wrapped so removal is by this registration, not by lambda equality.
        val registration: () -> Unit = { onRunning() }
        runningSubscribers.add(registration)
        return { runningSubscribers.remove(registration) }
    }
}
