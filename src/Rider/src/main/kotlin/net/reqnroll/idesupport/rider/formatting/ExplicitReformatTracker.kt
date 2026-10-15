package net.reqnroll.idesupport.rider.formatting

import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * Remembers which files the user has just asked to reformat explicitly (Code > Reformat Code, or the
 * Reformat File dialog), so [ReqnrollFormattingService] can tell a user-invoked reformat from an
 * implicit one (format on save, on paste, ...) when the server rejects it (issue #1172).
 *
 * The platform gives a formatting task no such signal: `FormatOnSaveAction` and `ReformatCodeAction`
 * both run `ReformatCodeProcessor`, and `FormattingContext` / `AsyncFormattingRequest` look the same for
 * both. The only difference is the action the user invoked, which [ExplicitReformatActionListener]
 * reports here. A mark is single-use and short-lived, so an implicit trigger is never mistaken for an
 * explicit one unless it hits the same file right after an explicit reformat that was itself rejected.
 */
internal class ExplicitReformatTracker(private val nanoTime: () -> Long = System::nanoTime) {
    private val marks = ConcurrentHashMap<String, Long>()

    /** Records that [paths] (files or directories, `/`-separated) are being reformatted on explicit request. */
    fun mark(paths: Collection<String>) {
        val now = nanoTime()
        for (path in paths) marks[path.trimEnd('/')] = now
    }

    /**
     * `true` when [filePath] was marked, directly or through a marked ancestor directory, within the last
     * [TTL_SECONDS] seconds; consumes the matching mark so one explicit action authorizes one modal.
     */
    fun consume(filePath: String): Boolean {
        val now = nanoTime()
        marks.entries.removeIf { now - it.value > TimeUnit.SECONDS.toNanos(TTL_SECONDS) }
        val match = marks.keys.firstOrNull { filePath == it || filePath.startsWith("$it/") } ?: return false
        return marks.remove(match) != null
    }

    companion object {
        /** Long enough for the reformat to reach the server after the action; short enough to forget an abandoned one. */
        const val TTL_SECONDS = 15L

        val instance = ExplicitReformatTracker()
    }
}
