package com.reqnroll.ide.rider.lsp.project

import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.Key
import com.intellij.platform.lsp.api.LspServer
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerManagerListener
import com.intellij.platform.lsp.api.LspServerState
import com.reqnroll.ide.rider.lsp.ReqnrollLspServerSupportProvider
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * Runs [action] once the Reqnroll LSP server reaches [LspServerState.Running] — immediately if
 * it's already there, otherwise deferred via [LspServerManagerListener].
 *
 * Sending any custom `reqnroll`-prefixed notification before the server completes its LSP
 * initialize/initialized handshake gets it dropped and logged as an "Unexpected notification" by
 * OmniSharp's `LspServerReceiver` — confirmed live. The server itself may not even have been
 * *started* yet: it's launched lazily from `ReqnrollLspServerSupportProvider.fileOpened`, but
 * Rider's `runnableProjectsModel.projects` reactive property (subscribed to by
 * [ReqnrollRunnableProjectsListener] and [ReqnrollProjectFilesSync]) fires on its own schedule at
 * project open, independent of any file being opened — so its very first `advise` callback almost
 * always races ahead of server startup. Route every such push through this gate rather than
 * sending directly from an `advise`/model-change callback.
 *
 * Registers the deferred listener against a fresh, per-call [com.intellij.openapi.Disposable] (a
 * child of [Project], not the project itself) and disposes it immediately once the action runs —
 * confirmed by decompiling `LspServerManager` that there is no `removeLspServerManagerListener` at
 * all; the *only* way to deregister is via the parent `Disposable` passed at registration. Without
 * this, a listener registered here would stay subscribed for the rest of the project's lifetime
 * even after firing once, and would fire `action` again on every later "Running" transition —
 * including after a manual LSP server restart (a real, user-reachable action via the status-bar
 * widget), silently turning "run once when ready" into "run every time the server (re)starts."
 *
 * A deferred action runs on the thread that moves the server to Running — in Rider 2024.3.5 that's
 * the `whenComplete` of the LSP `initialize` request's future, i.e. a platform background thread,
 * not the EDT. See [ServerReadinessGate] for the check-then-register race and the optional
 * per-project coalescing of pending actions (issue #987).
 */
object ReqnrollLspServerReadiness {
    private val GATE_KEY = Key.create<ServerReadinessGate>("Reqnroll.ServerReadinessGate")

    fun runWhenRunning(project: Project, coalesceKey: String? = null, action: () -> Unit) {
        gateFor(project).runWhenRunning(coalesceKey, action)
    }

    private fun gateFor(project: Project): ServerReadinessGate =
        project.getUserData(GATE_KEY) ?: synchronized(this) {
            project.getUserData(GATE_KEY)
                ?: ServerReadinessGate(LspServerManagerStateSource(project)).also { project.putUserData(GATE_KEY, it) }
        }

    /** The real [ServerStateSource]: the Reqnroll server's state as [LspServerManager] reports it. */
    private class LspServerManagerStateSource(private val project: Project) : ServerStateSource {
        private val manager get() = LspServerManager.getInstance(project)

        override fun isRunning(): Boolean =
            manager.getServersForProvider(ReqnrollLspServerSupportProvider::class.java).firstOrNull()?.state ==
                LspServerState.Running

        override fun subscribe(onRunning: () -> Unit): () -> Unit {
            val listenerLifetime = Disposer.newDisposable(project, "ReqnrollLspServerReadiness.runWhenRunning")
            manager.addLspServerManagerListener(
                object : LspServerManagerListener {
                    override fun serverStateChanged(lspServer: LspServer) {
                        if (lspServer.providerClass == ReqnrollLspServerSupportProvider::class.java &&
                            lspServer.state == LspServerState.Running
                        ) {
                            onRunning()
                        }
                    }
                },
                listenerLifetime,
                // `true` would not help: decompiled, it replays only a ShutdownUnexpectedly state
                // (never Running) plus fileOpened events, and through the multicaster to every
                // registered listener, not just this one. ServerReadinessGate re-checks instead.
                false,
            )
            return { Disposer.dispose(listenerLifetime) }
        }
    }
}

/** The LSP server's state as [ServerReadinessGate] sees it; a seam so the gate is testable without a platform. */
internal interface ServerStateSource {
    fun isRunning(): Boolean

    /**
     * Calls [onRunning] on every transition to Running, on whatever thread the transition happens,
     * until the returned handle is invoked.
     */
    fun subscribe(onRunning: () -> Unit): () -> Unit
}

/**
 * Platform-free core of [ReqnrollLspServerReadiness.runWhenRunning] (issue #987).
 *
 * The Running transition happens on a platform background thread (the `initialize` future's
 * `whenComplete`), while callers check the state from the EDT, so the transition can
 * land after the state check but before the listener is registered — and the platform never
 * replays a Running state to a newly added listener. Re-checking the state after registering
 * closes that window; a once-only guard keeps the re-check and a concurrent notification from
 * both running the action.
 */
internal class ServerReadinessGate(private val server: ServerStateSource) {
    private val pendingKeys: MutableSet<String> = ConcurrentHashMap.newKeySet()

    /**
     * Runs [action] now if the server is running, otherwise once when it reaches Running.
     *
     * While a deferred action for [coalesceKey] is still waiting, further requests with the same
     * key are dropped, so only pass a key when the action reads its state when it *runs* rather
     * than capturing it at request time.
     */
    fun runWhenRunning(coalesceKey: String? = null, action: () -> Unit) {
        if (server.isRunning()) {
            action()
            return
        }

        if (coalesceKey != null && !pendingKeys.add(coalesceKey)) {
            return
        }

        val fired = AtomicBoolean(false)
        val unsubscribe = AtomicReference<(() -> Unit)?>(null)

        fun fire() {
            if (!fired.compareAndSet(false, true)) return
            unsubscribe.getAndSet(null)?.invoke()
            if (coalesceKey != null) pendingKeys.remove(coalesceKey)
            action()
        }

        // A lambda, not `::fire`: callable references to a local fun compare equal across calls,
        // so a source that unsubscribes by equality could drop another request's listener.
        val handle = try {
            server.subscribe { fire() }
        } catch (e: Throwable) {
            // Never registered, so nothing would release the key: later requests would be dropped.
            if (coalesceKey != null) pendingKeys.remove(coalesceKey)
            throw e
        }
        unsubscribe.set(handle)
        // Fired while subscribe() was still registering, before the handle was published above.
        if (fired.get()) unsubscribe.getAndSet(null)?.invoke()

        if (server.isRunning()) fire()
    }
}
