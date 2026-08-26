package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import java.util.concurrent.ConcurrentHashMap

/**
 * Per-(uri, line) cache of resolved `reqnroll/resolveTestTargets` results (issue #495).
 *
 * Unlike VS's classic CodeLens (async per-line data points) and VS Code's `CodeLensProvider`
 * (`provideCodeLenses` + lazy `resolveCodeLens`), IntelliJ's `CodeVisionProvider.computeCodeVision`
 * has no visible-range parameter and no per-entry resolve phase — it's always asked for the *whole*
 * document, every time CodeVision recomputes (issue #495's platform survey). Rider therefore can't
 * skip resolving off-screen scenarios the way the other two clients now do; the only lever available
 * is skipping the RPC itself for a scenario whose *identity* hasn't changed since the last recompute.
 *
 * [identity] is the caller's cheap proxy for "would this scenario resolve to something different?" —
 * [RunLensSupport] builds it from the symbol's Scenario/Scenario-Outline kind plus its name. A
 * `computeCodeVision` call still walks every symbol in the document (that part of the cost is
 * unavoidable on this platform), but only re-sends `reqnroll/resolveTestTargets` for scenarios whose
 * identity actually changed, instead of for all of them unconditionally.
 */
internal object RunTestTargetCache {
    private data class Key(val uri: String, val line: Int)
    private data class Entry(val identity: String, val targets: List<ScenarioTestTargetItem>)

    private val entries = ConcurrentHashMap<Key, Entry>()

    /** Returns the cached targets for (uri, line) only if they were cached under the same [identity]; null otherwise (never resolved, or the scenario there changed). */
    fun get(uri: String, line: Int, identity: String): List<ScenarioTestTargetItem>? {
        val entry = entries[Key(uri, line)] ?: return null
        return if (entry.identity == identity) entry.targets else null
    }

    /** Records the resolved [targets] for (uri, line) under [identity], for [get] to reuse until that identity changes or the cache is invalidated. */
    fun put(uri: String, line: Int, identity: String, targets: List<ScenarioTestTargetItem>) {
        entries[Key(uri, line)] = Entry(identity, targets)
    }

    /** Drops every cached line for [uri] — real staleness signal, e.g. the file's bindings changed underneath an unchanged scenario name. */
    fun invalidateFile(uri: String) {
        entries.keys.removeIf { it.uri == uri }
    }

    /** Drops every cached entry for every file — called from `reqnroll/refreshCodeLenses` (see `ReqnrollCodeLensRefreshInterceptor`), the same workspace-wide signal `HookCodeVisionProvider`/`StepUsagesCodeVisionProvider` already act on. */
    fun invalidateAll() = entries.clear()
}
