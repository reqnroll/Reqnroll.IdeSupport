package net.reqnroll.idesupport.rider.telemetry

/**
 * Reduces a JVM stack to a privacy-safe, bounded attribution string (issue #1199), the Kotlin counterpart of
 * the server's `ExceptionStackSanitizer` (issue #620) and so the same `StackFrames` contract: innermost frame
 * first, one entry per line, at most [MAX_FRAMES] entries and [MAX_LENGTH] characters.
 *
 * Only frames of this plugin (`net.reqnroll.idesupport.rider.`) are named, as `package.Class.method:line`
 * (compiler-generated inner/lambda classes folded into the declaring class and member). File names are never
 * emitted — only the class, method and integer line — and the exception's message, type and causes are never
 * read. Every run of other frames (JDK, IntelliJ platform, third party) collapses to one [EXTERNAL_PLACEHOLDER];
 * the plugin's own telemetry/logging helper frames are skipped, as they are in `Source`. `null` when no frame
 * of the plugin remains.
 */
internal object ClientStackSanitizer {
    const val EXTERNAL_PLACEHOLDER = "[external]"
    const val MAX_FRAMES = 8
    const val MAX_LENGTH = 1024

    private const val PLUGIN_PACKAGE_PREFIX = "net.reqnroll.idesupport.rider."
    private val SKIPPED_PACKAGES = listOf(
        "net.reqnroll.idesupport.rider.telemetry.",
        "net.reqnroll.idesupport.rider.logging.",
    )
    private val LAMBDA_ENTRY_POINTS = setOf("invoke", "invokeSuspend", "run", "call", "accept", "apply")

    fun sanitize(throwable: Throwable): String? = sanitize(throwable.stackTrace)

    fun sanitize(frames: Array<StackTraceElement>): String? = try {
        val entries = ArrayList<String>()
        var sawOwnFrame = false
        for (frame in frames) {
            if (entries.size >= MAX_FRAMES) break
            val className = frame.className
            if (SKIPPED_PACKAGES.any { className.startsWith(it) }) continue
            val own = format(frame)
            if (own != null) {
                sawOwnFrame = true
                entries.add(own)
            } else if (entries.lastOrNull() != EXTERNAL_PLACEHOLDER) {
                entries.add(EXTERNAL_PLACEHOLDER)
            }
        }
        if (!sawOwnFrame) {
            null
        } else {
            val result = StringBuilder()
            for (entry in entries) {
                val added = (if (result.isEmpty()) 0 else 1) + entry.length
                if (result.length + added > MAX_LENGTH) break
                if (result.isNotEmpty()) result.append('\n')
                result.append(entry)
            }
            result.toString().ifEmpty { null }
        }
    } catch (_: Throwable) {
        null
    }

    private fun format(frame: StackTraceElement): String? {
        val className = frame.className
        if (!className.startsWith(PLUGIN_PACKAGE_PREFIX)) return null
        val outer = className.substringBefore('$')
        val nested = className.substringAfter('$', "").split('$').firstOrNull { it.isNotEmpty() && !it.first().isDigit() }
        var method = identifier(frame.methodName)
        if (className.contains('$') && (method in LAMBDA_ENTRY_POINTS || frame.methodName.startsWith("lambda$"))) {
            // "Outer$findUsages$1.invoke" -> "Outer.findUsages{lambda}"
            method = (if (nested != null) identifier(nested) else method) + "{lambda}"
        }
        val line = if (frame.lineNumber > 0) ":${frame.lineNumber}" else ""
        return "${identifier(outer, allowDots = true)}.$method$line"
    }

    private fun identifier(name: String, allowDots: Boolean = false): String =
        name.filter { it.isLetterOrDigit() || it == '_' || (allowDots && it == '.') }
}
