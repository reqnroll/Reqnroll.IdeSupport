package net.reqnroll.idesupport.rider.lsp

import net.reqnroll.idesupport.rider.telemetry.ClientExceptionTelemetry
import net.reqnroll.idesupport.rider.telemetry.ClientStackSanitizer

/**
 * What the .NET runtime printed to the language server's stderr as it died, reduced to telemetry-safe values:
 * the kind of report and the outermost and innermost exception (full type name, scrubbed message, product-only
 * frames). The Kotlin counterpart of `CrashReport` in `Reqnroll.IdeSupport.Common` and the VS Code extension's
 * `serverCrashReport.ts`, with the same property contract; keep the three in step.
 */
internal data class ServerCrashReport(
    val kind: String,
    val outer: CrashedException,
    val inner: CrashedException?,
    val innerExceptionCount: Int,
) {
    data class CrashedException(val type: String?, val message: String?, val stackFrames: String?)

    /** The report as event properties; unknown values are omitted. */
    fun toProperties(): Map<String, Any?> = buildMap {
        put(CRASH_KIND, kind)
        putIfPresent(EXCEPTION_TYPE, outer.type)
        putIfPresent(MESSAGE, outer.message)
        putIfPresent(STACK_FRAMES, outer.stackFrames)
        if (inner != null) {
            put(INNER_EXCEPTION_COUNT, innerExceptionCount)
            putIfPresent(INNER_EXCEPTION_TYPE, inner.type)
            putIfPresent(INNER_MESSAGE, inner.message)
            putIfPresent(INNER_STACK_FRAMES, inner.stackFrames)
        }
    }

    private fun MutableMap<String, Any?>.putIfPresent(key: String, value: String?) {
        if (!value.isNullOrEmpty()) put(key, value)
    }

    companion object {
        const val CRASH_KIND = "CrashKind"
        const val EXCEPTION_TYPE = "ExceptionType"
        const val MESSAGE = "Message"
        const val STACK_FRAMES = "StackFrames"
        const val INNER_EXCEPTION_TYPE = "InnerExceptionType"
        const val INNER_MESSAGE = "InnerMessage"
        const val INNER_STACK_FRAMES = "InnerStackFrames"
        const val INNER_EXCEPTION_COUNT = "InnerExceptionCount"

        const val KIND_UNHANDLED_EXCEPTION = "UnhandledException"
        const val KIND_STACK_OVERFLOW = "StackOverflow"
        const val KIND_FATAL_ERROR = "FatalError"
        const val KIND_FAIL_FAST = "FailFast"
    }
}

/**
 * Parses the crash report the .NET runtime writes to stderr when the server dies. The unhandled-exception text is
 * `Exception.ToString()`: the outer header, one ` ---> ` header per nested exception, then the innermost
 * exception's frames, an `--- End of inner exception stack trace ---` line, the next one out's frames, and so
 * on, ending with the outermost exception's frames. Never throws.
 */
internal object ServerCrashReportParser {
    private const val MAX_MESSAGE_LENGTH = 128
    private const val PRODUCT_PREFIX = "Reqnroll.IdeSupport."
    private const val END_OF_INNER_STACK = "--- End of inner exception stack trace ---"
    private const val INNER_PREFIX = "--->"

    private val HEADERS = listOf(
        "Unhandled exception." to ServerCrashReport.KIND_UNHANDLED_EXCEPTION,
        "Stack overflow." to ServerCrashReport.KIND_STACK_OVERFLOW,
        "Fatal error." to ServerCrashReport.KIND_FATAL_ERROR,
        "Process terminated." to ServerCrashReport.KIND_FAIL_FAST,
    )

    // Generic arguments print in brackets after the method name, as Method[T] or, in a stack-overflow or
    // FailFast report, Method[[System.__Canon, System.Private.CoreLib, ...]], which contains spaces.
    private val FRAME = Regex("""^\s*at ([^\s(\[]+)(?:\[[^(]*])?\([^)]*\)(?: in .*:line (\d+))?""")
    private val INNER_ORDINAL = Regex("""^\(Inner Exception #\d+\)\s*""")
    private val TYPE_NAME = Regex("""^[A-Za-z_][A-Za-z0-9_.`+]*$""")
    private val CONSTRUCTOR = Regex("""\.(\.c?ctor)$""")

    private fun headerOf(line: String): Pair<String, String>? {
        val trimmed = line.trimStart()
        return HEADERS.firstOrNull { trimmed.startsWith(it.first) }
    }

    /** Whether [line] starts one of the runtime's crash reports. */
    fun isCrashHeader(line: String): Boolean = headerOf(line) != null

    private fun identifier(name: String): String {
        val cut = name.indexOfFirst { it == '<' || it == '(' }
        return (if (cut >= 0) name.substring(0, cut) else name).filter { it.isLetterOrDigit() || it == '_' }
    }

    // "<Foo>b__0_1" -> "Foo{lambda}"; "<<Foo>b__0>g__Local|1_0" -> "Foo{local}"; "<Foo>d__3" -> "Foo"
    private fun fromCompilerName(compilerName: String): String {
        var start = 0
        while (start < compilerName.length && compilerName[start] == '<') start++
        val end = compilerName.indexOf('>', start)
        val inner = if (end > start) identifier(compilerName.substring(start, end)) else ""
        return when {
            inner.isEmpty() -> "{anonymous}"
            ">g__" in compilerName -> "$inner{local}"
            ">b__" in compilerName -> "$inner{lambda}"
            else -> inner
        }
    }

    private fun normalizeMethodName(methodName: String, declaringTypeName: String): String {
        if (methodName.startsWith("<")) return fromCompilerName(methodName)
        if (declaringTypeName.startsWith("<")) {
            val fromType = fromCompilerName(declaringTypeName)
            if (fromType != "{anonymous}") return fromType
        }
        return when (methodName) {
            ".ctor" -> "{ctor}"
            ".cctor" -> "{cctor}"
            else -> identifier(methodName)
        }
    }

    /** Formats one text frame like the server's `ExceptionStackSanitizer.FormatProductFrameText`; null for a non-product frame. */
    fun formatProductFrame(qualifiedName: String, line: Int): String? {
        if (!qualifiedName.startsWith(PRODUCT_PREFIX)) return null
        // ".ctor"/".cctor" contain the separator themselves; keep them together.
        val ctor = CONSTRUCTOR.find(qualifiedName)
        val head = if (ctor != null) qualifiedName.substring(0, ctor.range.first) else qualifiedName
        val segments = head.split('.', '+').toMutableList()
        if (ctor != null) segments += ctor.groupValues[1]
        if (segments.size < 2) return null

        val method = segments.last()
        val declaringType = segments[segments.size - 2]
        val kept = segments.dropLast(1)
            .filter { it.isNotEmpty() && !it.startsWith("<") }
            .map { identifier(it.replace(Regex("""`\d+$"""), "")) }
            .toMutableList()
        kept += normalizeMethodName(method, declaringType)
        return kept.joinToString(".") + if (line > 0) ":$line" else ""
    }

    /** Joins formatted frames (null = non-product) under the shared `StackFrames` contract. */
    fun joinFrames(frames: List<String?>): String? {
        val entries = mutableListOf<String>()
        var sawProduct = false
        for (name in frames) {
            if (entries.size >= ClientStackSanitizer.MAX_FRAMES) break
            if (name == null) {
                if (entries.lastOrNull() != ClientStackSanitizer.EXTERNAL_PLACEHOLDER) {
                    entries += ClientStackSanitizer.EXTERNAL_PLACEHOLDER
                }
            } else {
                sawProduct = true
                entries += name
            }
        }
        if (!sawProduct) return null

        val result = StringBuilder()
        for (entry in entries) {
            val added = (if (result.isEmpty()) 0 else 1) + entry.length
            if (result.length + added > ClientStackSanitizer.MAX_LENGTH) break
            if (result.isNotEmpty()) result.append('\n')
            result.append(entry)
        }
        return result.toString().ifEmpty { null }
    }

    private fun scrub(text: String?): String? {
        val trimmed = text?.trim()
        if (trimmed.isNullOrEmpty()) return null
        return ClientExceptionTelemetry.scrubMessage(trimmed.take(4 * MAX_MESSAGE_LENGTH)).take(MAX_MESSAGE_LENGTH)
    }

    // "Namespace.TypeName: message" -> type and scrubbed message; a header that does not start with a type name
    // keeps no type, and its text is treated as the message.
    private fun toException(header: String, stackFrames: String?): ServerCrashReport.CrashedException {
        val separator = header.indexOf(": ")
        val candidate = if (separator > 0) header.substring(0, separator) else header
        if (!TYPE_NAME.matches(candidate)) return ServerCrashReport.CrashedException(null, scrub(header), stackFrames)
        val message = if (separator > 0) scrub(header.substring(separator + 2)) else null
        return ServerCrashReport.CrashedException(candidate, message, stackFrames)
    }

    /** Parses the last crash report in [lines]; null when there is none. Never throws. */
    fun parse(lines: List<String>): ServerCrashReport? =
        try {
            parseCore(lines)
        } catch (e: Exception) {
            null
        }

    private fun parseCore(lines: List<String>): ServerCrashReport? {
        val start = lines.indexOfLast(::isCrashHeader)
        if (start < 0) return null

        val headerLine = lines[start].trimStart()
        val (prefix, kind) = headerOf(headerLine)!!
        val headers = mutableListOf<String>()
        if (kind != ServerCrashReport.KIND_STACK_OVERFLOW) headers += headerLine.substring(prefix.length).trim()
        val sections = mutableListOf(mutableListOf<String?>())
        var inFrames = false

        for (i in start + 1 until lines.size) {
            val line = lines[i]
            val trimmed = line.trim()
            val frame = FRAME.find(line)
            if (frame != null) {
                inFrames = true
                val number = frame.groupValues[2].toIntOrNull() ?: 0
                val formatted = formatProductFrame(frame.groupValues[1], number)
                val section = sections.last()
                // A stack overflow prints its repeating block once, but consecutive repeats of one frame add nothing.
                if (section.isEmpty() || section.last() != formatted) section += formatted
            } else if (trimmed == END_OF_INNER_STACK) {
                sections += mutableListOf<String?>()
            } else if (!inFrames && kind != ServerCrashReport.KIND_STACK_OVERFLOW && trimmed.startsWith(INNER_PREFIX)) {
                headers += INNER_ORDINAL.replace(trimmed.substring(INNER_PREFIX.length).trim(), "")
            } else if (!inFrames && headers.isNotEmpty() && trimmed.isNotEmpty()) {
                // A message spanning several lines continues the latest header.
                headers[headers.size - 1] = headers.last() + " " + trimmed
            }
        }

        val outerFrames = joinFrames(sections.last())
        if (kind == ServerCrashReport.KIND_STACK_OVERFLOW) {
            return ServerCrashReport(kind, ServerCrashReport.CrashedException(null, null, outerFrames), null, 0)
        }

        val outer = if (kind == ServerCrashReport.KIND_FAIL_FAST) {
            ServerCrashReport.CrashedException(null, scrub(headers[0]), outerFrames)
        } else {
            toException(headers[0], outerFrames)
        }
        if (headers.size < 2) return ServerCrashReport(kind, outer, null, 0)

        val innerFrames = if (sections.size > 1) joinFrames(sections[0]) else null
        return ServerCrashReport(kind, outer, toException(headers.last(), innerFrames), headers.size - 1)
    }
}

/**
 * Collects stderr from the last crash-report header onwards (bounded), so the report can be parsed once the
 * server has died. Text arrives in chunks that need not end at a line break. Thread-safe.
 */
internal class ServerCrashReportCollector {
    private val lines = mutableListOf<String>()
    private val partial = StringBuilder()
    private var capturing = false

    @Synchronized
    fun onText(text: String) {
        partial.append(text)
        while (true) {
            val newline = partial.indexOf("\n")
            if (newline < 0) break
            onLine(partial.substring(0, newline).trimEnd('\r'))
            partial.delete(0, newline + 1)
        }
        // A runaway line without a break must not grow without bound.
        if (partial.length > MAX_LINE_LENGTH) partial.setLength(MAX_LINE_LENGTH)
    }

    private fun onLine(line: String) {
        if (ServerCrashReportParser.isCrashHeader(line)) {
            lines.clear()
            capturing = true
        }
        if (capturing && lines.size < MAX_LINES) lines += line.take(MAX_LINE_LENGTH)
    }

    /** The crash report collected so far (including a final line without a break), or null when no header was seen. */
    @Synchronized
    fun parse(): ServerCrashReport? {
        if (partial.isNotEmpty()) {
            onLine(partial.toString())
            partial.setLength(0)
        }
        return if (capturing) ServerCrashReportParser.parse(lines.toList()) else null
    }

    companion object {
        const val MAX_LINES = 400
        const val MAX_LINE_LENGTH = 2048
    }
}
