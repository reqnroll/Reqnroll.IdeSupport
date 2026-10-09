package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import java.util.concurrent.atomic.AtomicLong

/**
 * Applies only the response of the most recent request (issue #990). The `.feature` inlay-hint,
 * folding, tag-link, breadcrumb and Structure View refreshes each send their LSP request on a
 * pooled thread and render the result on the EDT. Refreshes overlap (the debounced edit, the
 * server's `workspace/inlayHint/refresh`, editor creation), and nothing orders their completion,
 * so without this a slower, older response could land last and replace newer output.
 *
 * [fetchThenApply] takes a ticket before the request is sent, and the response is applied only
 * if no later request was started and [invalidate] was not called in the meantime. The check
 * runs on the EDT, right before applying, so it is ordered with the other renders. Callers call
 * [invalidate] when the document changes: a response then describes text that no longer exists,
 * and the edit always schedules a newer (debounced) request that replaces it.
 *
 * A null response (no server running, a timeout, or a failed or cancelled request; the server
 * itself answers "nothing here" with an empty list) is never applied, so the current output is
 * kept instead of being cleared.
 *
 * One instance per editor (or per document), shared by all of that editor's refreshes. The
 * thread hooks are parameters only so tests can run both sides by hand.
 */
internal class LatestResponseGate(
    private val runInBackground: (() -> Unit) -> Unit = { ApplicationManager.getApplication().executeOnPooledThread(Runnable(it)) },
    private val runOnEdt: (() -> Unit) -> Unit = { ApplicationManager.getApplication().invokeLater(Runnable(it), ModalityState.any()) },
) {
    private val generation = AtomicLong()

    /** Marks every request still in flight as outdated, e.g. because the document was edited. */
    fun invalidate() {
        generation.incrementAndGet()
    }

    /** Runs [fetch] in the background, then [apply] on the EDT, unless the result is null or a later request or [invalidate] superseded it. */
    fun <T : Any> fetchThenApply(fetch: () -> T?, apply: (T) -> Unit) {
        val ticket = generation.incrementAndGet()
        runInBackground {
            val result = fetch() ?: return@runInBackground
            runOnEdt {
                if (generation.get() == ticket) apply(result)
            }
        }
    }
}
