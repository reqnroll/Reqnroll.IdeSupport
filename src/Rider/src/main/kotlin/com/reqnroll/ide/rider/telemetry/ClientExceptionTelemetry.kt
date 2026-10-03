package com.reqnroll.ide.rider.telemetry

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProcessCanceledException
import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import java.util.concurrent.CancellationException

/**
 * Client-side exception telemetry for the Rider plugin (issue #621): reports exceptions caught in the
 * plugin's own code as the same `UnhandledException` event the LSP server sends for its exceptions
 * (`LspErrorTelemetryService.MonitorError`), so error-rate queries stay uniform. Events from here carry
 * `ExceptionOrigin = "Client"` (absent on server-originated ones); [RiderTelemetryTransmitter] stamps
 * `IdeClient = "rider"` like on every other event.
 *
 * **Capture point.** [com.reqnroll.ide.rider.logging.ReqnrollDebugLogger.warn]/`error` call [report]
 * whenever they are given a throwable — that is where this plugin already records every exception it
 * catches and degrades on (LSP request failures, test-runner/MTP-stub failures, server-path resolution,
 * gutter marks), so one hook covers them all without touching each catch block. The hook is deliberately
 * broad (Warning and Error): the dedupe/cap below bound the volume. Exceptions the plugin does *not* catch
 * are left to the IntelliJ platform's own error reporting and are not covered.
 *
 * **Not reported:** a `ResponseErrorException` (an LSP error response — the server already reports its own
 * failures and the text can embed step or expression text), including one wrapped as a cause; and
 * cancellations (`ProcessCanceledException`, `CancellationException`, which includes the timeout
 * cancellations of requests such as rename — a timeout is not a plugin failure).
 *
 * **Privacy contract** (same bar as the server's `UnhandledException`, issues #620/#843, hardened because
 * client exception text is arbitrary): only the exception class name, a scrubbed message capped at
 * [MAX_MESSAGE_LENGTH] characters and a closed `Source` (the simple name of the topmost plugin class on the
 * stack) are sent. The message has every quoted substring dropped, URLs removed, and absolute, UNC, home,
 * relative and bare source/config file-name paths redacted. The stack trace itself is never sent
 * (issue #620 is open; assume not).
 *
 * Reporting must never throw into, or slow, the code it observes: it is wrapped, re-entrancy guarded,
 * handed to a pooled thread, deduplicated per session and capped. [RiderTelemetryTransmitter.transmit]
 * honours `REQNROLL_TELEMETRY_ENABLED` (Rider has no IDE-level telemetry opt-out) and mirrors to the debug log.
 */
object ClientExceptionTelemetry {
    internal const val EXCEPTION_ORIGIN_CLIENT = "Client"
    internal const val UNHANDLED_EXCEPTION = RiderTelemetryTransmitter.UNHANDLED_EXCEPTION

    /** Most `UnhandledException` events one IDE session may send, however many distinct errors occur. */
    internal const val MAX_EVENTS_PER_SESSION = 20
    internal const val MAX_MESSAGE_LENGTH = 128

    private const val PLUGIN_PACKAGE_PREFIX = "com.reqnroll.ide.rider."
    private val SKIPPED_SOURCE_PACKAGES = listOf("com.reqnroll.ide.rider.telemetry.", "com.reqnroll.ide.rider.logging.")

    // Source/config file extensions whose bare names (or relative paths ending in them) are redacted.
    private const val FILE_EXTENSIONS =
        "feature|cs|csproj|fsproj|vbproj|sln|slnx|slnf|json|md|xml|config|props|targets|txt|yml|yaml|ts|js|kt|java|ps1|sh|dll|exe|log|trx|runsettings|vb|fs|razor|cshtml|proj|projitems|shproj|nuspec|resx|ini|toml|lock"

    // Directory segments may contain interior spaces ("C:\Users\John Smith\x") but never start or end with one,
    // so a path cannot swallow the words (or a second path) that follow it. Mirrors TelemetryScrubber (server).
    private const val SEG = """(?:[^\\/\s"'<>|*?:](?:[^\\/\r\n"'<>|*?:]*[^\\/\s"'<>|*?:])?[\\/])*"""
    private const val LAST = """[^\\/\s"'<>|*?:]*"""

    // Order matters: quoted text first, then URLs, then path shapes from most to least specific.
    // Double quotes: first to LAST quote on the line (V8 embeds a JSON snippet unescaped, so nested quotes must
    // not end the span early); an unbalanced quote drops the rest of the line.
    private val QUOTED = Regex("""["][^\r\n]*["]|["][^\r\n]*|`[^`\r\n]*`|'[^'\r\n]{2,}'""")
    private val URL_PATTERN = Regex("""\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s"'<>]+""")
    private val PATH_PATTERNS = listOf(
        Regex("""[A-Za-z]:[\\/]$SEG$LAST"""), // drive-letter
        Regex("""\\\\$SEG$LAST"""), // UNC
        Regex("""(?:~|%[A-Za-z_]\w*%|\$\{?[A-Za-z_]\w*\}?)[\\/]$SEG$LAST"""), // ~, %VAR%, $VAR
        Regex("""(?<![\w~%$}.])/(?=[^\s/])$SEG$LAST"""), // POSIX absolute
        Regex("""(?<![\w.])\.{1,2}[\\/][^\s"'<>|*?:]*"""), // ./x, ../x
        Regex("""(?<!\w)(?:[\w.\-]+[\\/])*[\w.\-]+\.(?:$FILE_EXTENSIONS)\b"""), // bare names / relative paths with a known extension
    )

    @Volatile
    private var session = Reporter { name, props -> sendDefault(name, props) }

    /** Reports [throwable] as a client-side `UnhandledException`. Never throws. */
    fun report(throwable: Throwable) = session.report(throwable)

    /** Swaps the session-wide reporter (tests only, to observe what [report] sends); returns the previous one. */
    internal fun replaceSessionForTests(replacement: Reporter): Reporter = session.also { session = replacement }

    /**
     * Removes everything path- or content-shaped from exception text: quoted substrings (`<text>`), URLs
     * (`<url>`, query string and fragment included) and paths (`<path>`).
     */
    internal fun scrubMessage(text: String): String {
        var result = URL_PATTERN.replace(QUOTED.replace(text, "<text>"), "<url>")
        for (pattern in PATH_PATTERNS) result = pattern.replace(result, "<path>")
        return result
    }

    /**
     * Builds the `UnhandledException` properties for [throwable]; pure and exposed for tests. The stack is
     * only consulted for the `Source` attribution, never transmitted.
     */
    internal fun buildProperties(throwable: Throwable): Map<String, String> {
        // Bound the raw text first so a huge message cannot make the scrub slow, then cap what is sent.
        var message = scrubMessage((throwable.message ?: "").take(4 * MAX_MESSAGE_LENGTH))
        if (message.length > MAX_MESSAGE_LENGTH) message = message.substring(0, MAX_MESSAGE_LENGTH)
        val props = linkedMapOf(
            "ExceptionType" to throwable.javaClass.name,
            "Message" to message,
            "ExceptionOrigin" to EXCEPTION_ORIGIN_CLIENT,
        )
        resolveSource(throwable)?.let { props["Source"] = it }
        return props
    }

    /**
     * The simple name of the topmost plugin class on [throwable]'s stack — kotlin lambda/inner-class
     * suffixes (`Outer$inner$1`) folded into the outer class, telemetry/logging helper frames skipped —
     * so an error is attributable to a component without sending the stack. Null when no frame is ours.
     */
    internal fun resolveSource(throwable: Throwable): String? = try {
        throwable.stackTrace
            .asSequence()
            .map { it.className }
            .filter { it.startsWith(PLUGIN_PACKAGE_PREFIX) && SKIPPED_SOURCE_PACKAGES.none { p -> it.startsWith(p) } }
            .map { it.substringAfterLast('.').substringBefore('$') }
            .firstOrNull { it.isNotEmpty() }
    } catch (_: Exception) {
        null
    }

    private fun isCancellation(throwable: Throwable) =
        throwable is ProcessCanceledException || throwable is CancellationException

    /** True for an LSP error response, bare or as the cause of a wrapper (`ExecutionException`, `CompletionException`). */
    internal fun isResponseError(throwable: Throwable): Boolean =
        generateSequence(throwable) { it.cause?.takeIf { cause -> cause !== it } }.take(8).any { it is ResponseErrorException }

    private fun sendDefault(eventName: String, properties: Map<String, String>) {
        // Outside a running IDE (or in the platform's unit-test mode) there is nothing to send to: platform
        // fixtures that exercise failure paths must never reach the real Application Insights endpoint, and
        // tests of this class inject their own sender.
        val app = ApplicationManager.getApplication() ?: return
        if (app.isUnitTestMode) return
        // Never on the calling thread: it may be the EDT or a logging thread.
        app.executeOnPooledThread {
            try {
                RiderTelemetryTransmitter.transmit(eventName, properties)
            } catch (_: Throwable) {
                // Telemetry must never surface an error of its own.
            }
        }
    }

    /**
     * Sends each distinct (type, source, scrubbed message) at most once and no more than
     * [MAX_EVENTS_PER_SESSION] events in total. Events dropped as duplicates or by the cap are counted and the
     * count rides on the next event that is sent as `SuppressedCount` (events dropped after the cap is reached
     * are therefore never visible). Instance-based (and exposed) so the limits are testable.
     */
    internal class Reporter(private val send: (String, Map<String, String>) -> Unit) {
        private val seen = HashSet<String>()
        private var sent = 0
        private var suppressed = 0
        private val reentrancy = ThreadLocal<Boolean>()

        /** @return true if an event was handed to the sender. */
        fun report(throwable: Throwable): Boolean {
            if (reentrancy.get() == true) return false
            reentrancy.set(true)
            try {
                if (isCancellation(throwable) || isResponseError(throwable)) return false
                val props = LinkedHashMap(buildProperties(throwable))
                val key = "${props["Source"]}|${props["ExceptionType"]}|${props["Message"]}"
                synchronized(this) {
                    if (sent >= MAX_EVENTS_PER_SESSION || !seen.add(key)) {
                        suppressed++
                        return false
                    }
                    sent++
                    if (suppressed > 0) {
                        props["SuppressedCount"] = suppressed.toString()
                        suppressed = 0
                    }
                }
                send(UNHANDLED_EXCEPTION, props)
                return true
            } catch (_: Throwable) {
                // Telemetry must never break the code path it observes.
                return false
            } finally {
                reentrancy.remove()
            }
        }
    }
}
