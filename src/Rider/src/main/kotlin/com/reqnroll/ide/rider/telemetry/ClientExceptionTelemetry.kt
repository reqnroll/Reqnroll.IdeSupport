package com.reqnroll.ide.rider.telemetry

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.progress.ProcessCanceledException
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
 * gutter marks), so one hook covers them all without touching each catch block. Exceptions the plugin
 * does *not* catch are left to the IntelliJ platform's own error reporting and are not covered.
 *
 * **Privacy contract** (same bar as the server's `UnhandledException`, issues #620/#843): only the
 * exception class name, a path-scrubbed and length-capped message and a closed `Source` (the simple
 * name of the topmost plugin class on the stack) are sent. The stack trace itself is never sent
 * (issue #620 is open; assume not).
 *
 * Reporting must never throw into, or slow, the code it observes: it is wrapped, re-entrancy guarded,
 * deduplicated per session and capped. [RiderTelemetryTransmitter.transmit] already honours
 * `REQNROLL_TELEMETRY_ENABLED` and mirrors to the debug log.
 */
object ClientExceptionTelemetry {
    internal const val EXCEPTION_ORIGIN_CLIENT = "Client"
    internal const val UNHANDLED_EXCEPTION = RiderTelemetryTransmitter.UNHANDLED_EXCEPTION

    /** Most `UnhandledException` events one IDE session may send, however many distinct errors occur. */
    internal const val MAX_EVENTS_PER_SESSION = 20
    private const val MAX_REMEMBERED_KEYS = 200
    internal const val MAX_MESSAGE_LENGTH = 512

    private const val PLUGIN_PACKAGE_PREFIX = "com.reqnroll.ide.rider."
    private val SKIPPED_SOURCE_PACKAGES = listOf("com.reqnroll.ide.rider.telemetry.", "com.reqnroll.ide.rider.logging.")

    // Windows absolute/UNC paths and POSIX absolute paths; deliberately broad (over-redacting is safe).
    // Mirrors TelemetryScrubber.PathPattern in the LSP server (issue #843).
    private val PATH_PATTERN = Regex("""(?:[A-Za-z]:\\|\\\\|/)[^\s"'<>:*?|]+""")

    @Volatile
    private var session = Reporter { name, props -> sendDefault(name, props) }

    /** Reports [throwable] as a client-side `UnhandledException`. Never throws. */
    fun report(throwable: Throwable) = session.report(throwable)

    /** Swaps the session-wide reporter (tests only, to observe what [report] sends); returns the previous one. */
    internal fun replaceSessionForTests(replacement: Reporter): Reporter = session.also { session = replacement }

    /** Replaces filesystem-path-shaped substrings with `<path>`. */
    internal fun redactPaths(text: String): String = PATH_PATTERN.replace(text, "<path>")

    /**
     * Builds the `UnhandledException` properties for [throwable]; pure and exposed for tests. The stack is
     * only consulted for the `Source` attribution, never transmitted.
     */
    internal fun buildProperties(throwable: Throwable): Map<String, String> {
        var message = redactPaths(throwable.message ?: "")
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

    private fun sendDefault(eventName: String, properties: Map<String, String>) {
        // Never reach the real Application Insights endpoint from the platform test fixtures that
        // exercise failure paths; tests of this class inject their own sender.
        if (ApplicationManager.getApplication()?.isUnitTestMode == true) return
        RiderTelemetryTransmitter.transmit(eventName, properties)
    }

    /**
     * Sends each distinct (type, source, scrubbed message) at most once and no more than
     * [MAX_EVENTS_PER_SESSION] events in total. Instance-based (and exposed) so the limits are testable.
     */
    internal class Reporter(private val send: (String, Map<String, String>) -> Unit) {
        private val seen = HashSet<String>()
        private var sent = 0
        private val reentrancy = ThreadLocal<Boolean>()

        /** @return true if an event was handed to the sender. */
        fun report(throwable: Throwable): Boolean {
            if (reentrancy.get() == true) return false
            reentrancy.set(true)
            try {
                if (isCancellation(throwable)) return false
                val props = buildProperties(throwable)
                val key = "${props["Source"]}|${props["ExceptionType"]}|${props["Message"]}"
                synchronized(this) {
                    if (sent >= MAX_EVENTS_PER_SESSION || seen.size >= MAX_REMEMBERED_KEYS || !seen.add(key)) return false
                    sent++
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
