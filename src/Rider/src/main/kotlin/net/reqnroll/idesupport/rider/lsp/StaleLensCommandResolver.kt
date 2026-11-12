package net.reqnroll.idesupport.rider.lsp

import com.google.gson.JsonPrimitive
import net.reqnroll.idesupport.rider.codevision.HookLensSupport
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.Command
import kotlin.math.abs

/**
 * Checks a clicked CodeLens command against a fresh `textDocument/codeLens` response, and finds the lens it now
 * corresponds to when the document has changed since the lens was computed.
 *
 * Rider 2026.2 renders the server's lenses itself and caches them per file. The cache moves each lens's *position*
 * with the user's edits, but the command's *arguments* (the uri, line and character the click navigates from) stay
 * as the server computed them. When the cache is not refreshed after an edit that adds or removes lines, a click
 * runs with line numbers that point at the wrong place: a "step hooks" lens then lands on the `Scenario:` line and
 * shows the scenario's hooks, and the Feature lens lands on the line above `Feature:` and finds nothing.
 *
 * The match is made by kind (command name, argument count and the boolean flags) and, among lenses of that kind, by
 * the nearest click line, preferring the same title when two are equally near. That is right whenever the edits
 * since the stale response shifted lines by less than half the distance between two lenses of the same kind, which
 * covers adding or removing a few lines; a larger shift can pick a neighbouring lens.
 */
internal object StaleLensCommandResolver {
    sealed interface Resolution {
        /** The clicked command is in the fresh response unchanged. */
        data object Current : Resolution

        /** The clicked command was stale; [command] is the fresh lens command it corresponds to. */
        data class Rebased(val command: Command) : Resolution

        /** No fresh lens of the same kind exists any more. */
        data object Gone : Resolution
    }

    fun resolve(clicked: Command, fresh: List<CodeLens>): Resolution {
        val freshCommands = fresh.mapNotNull { it.command }
        val clickedIdentity = identity(clicked)
        if (freshCommands.any { identity(it) == clickedIdentity }) return Resolution.Current

        val clickedKind = kind(clicked)
        val clickedLine = HookLensSupport.argAsInt(clicked.arguments, 1) ?: return Resolution.Gone
        val best = freshCommands
            .filter { kind(it) == clickedKind }
            .mapNotNull { command -> HookLensSupport.argAsInt(command.arguments, 1)?.let { line -> command to line } }
            .minWithOrNull(
                compareBy<Pair<Command, Int>> { (_, line) -> abs(line - clickedLine) }
                    .thenBy { (command, _) -> if (command.title == clicked.title) 0 else 1 },
            )
        return if (best == null) Resolution.Gone else Resolution.Rebased(best.first)
    }

    /** What a command does: its name and arguments, with the platform's Gson primitives unwrapped. */
    private fun identity(command: Command): List<Any?> =
        listOf(command.command) + (command.arguments ?: emptyList()).map(::plain)

    /** Which lens a command belongs to, independent of where it points: name, argument count and boolean flags. */
    private fun kind(command: Command): List<Any?> {
        val args = command.arguments ?: emptyList()
        return listOf(command.command, args.size) + args.indices.map { HookLensSupport.argAsBoolean(args, it) }
    }

    private fun plain(raw: Any?): Any? = when {
        raw is JsonPrimitive && raw.isBoolean -> raw.asBoolean
        raw is JsonPrimitive && raw.isNumber -> raw.asLong
        raw is JsonPrimitive -> raw.asString
        raw is Number -> raw.toLong()
        else -> raw
    }
}
