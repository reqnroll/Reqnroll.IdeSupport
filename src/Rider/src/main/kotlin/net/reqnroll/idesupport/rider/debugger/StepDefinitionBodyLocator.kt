package net.reqnroll.idesupport.rider.debugger

/**
 * Finds where a C# step definition method's body starts, given the offset of the method's name (what the server's
 * `textDocument/definition` answer points at).
 *
 * The result is the line the debugger should run to: the line of the body's opening `{` (a Debug build puts a
 * sequence point there), or for an expression-bodied method the line where the expression after `=>` starts. The
 * parameter list is skipped by matching parentheses, ignoring string and character literals and comments, so a
 * default value or attribute argument containing `{` or `=>` does not end the search early.
 */
internal object StepDefinitionBodyLocator {
    /** The zero-based line of the body start, or null if [text] has no method body after [nameOffset]. */
    fun bodyStartLine(text: CharSequence, nameOffset: Int): Int? {
        val offset = bodyStartOffset(text, nameOffset) ?: return null
        return lineOf(text, offset)
    }

    internal fun bodyStartOffset(text: CharSequence, nameOffset: Int): Int? {
        if (nameOffset !in 0..text.length) return null
        var depth = 0
        var seenParameterList = false
        var i = nameOffset
        while (i < text.length) {
            val skipped = skipTrivia(text, i)
            if (skipped != i) {
                i = skipped
                continue
            }
            val c = text[i]
            when {
                c == '(' -> {
                    depth++
                    seenParameterList = true
                }
                c == ')' -> depth--
                depth > 0 -> Unit
                // A ';' at depth 0 before any body: an abstract, extern or partial declaration.
                c == ';' -> return null
                seenParameterList && c == '{' -> return i
                seenParameterList && c == '=' && i + 1 < text.length && text[i + 1] == '>' ->
                    return firstNonWhitespace(text, i + 2)
            }
            i++
        }
        return null
    }

    /** Skips a comment or a string/character literal starting at [start]; returns [start] if there is none. */
    private fun skipTrivia(text: CharSequence, start: Int): Int {
        val c = text[start]
        val next = if (start + 1 < text.length) text[start + 1] else '\u0000'
        return when {
            c == '/' && next == '/' -> indexOrEnd(text, "\n", start + 2)
            c == '/' && next == '*' -> indexOrEnd(text, "*/", start + 2).let { if (it < text.length) it + 2 else it }
            c == '@' && next == '"' -> skipVerbatimString(text, start + 2)
            c == '$' && next == '@' && start + 2 < text.length && text[start + 2] == '"' -> skipVerbatimString(text, start + 3)
            c == '"' -> skipQuoted(text, start + 1, '"')
            c == '\'' -> skipQuoted(text, start + 1, '\'')
            else -> start
        }
    }

    private fun skipQuoted(text: CharSequence, from: Int, quote: Char): Int {
        var i = from
        while (i < text.length) {
            when (text[i]) {
                '\\' -> i += 2
                quote -> return i + 1
                '\n' -> return i // unterminated: stop at the line end rather than swallow the file
                else -> i++
            }
        }
        return text.length
    }

    private fun skipVerbatimString(text: CharSequence, from: Int): Int {
        var i = from
        while (i < text.length) {
            if (text[i] == '"') {
                if (i + 1 < text.length && text[i + 1] == '"') i += 2 else return i + 1
            } else {
                i++
            }
        }
        return text.length
    }

    private fun indexOrEnd(text: CharSequence, token: String, from: Int): Int =
        text.indexOf(token, from).let { if (it < 0) text.length else it }

    private fun firstNonWhitespace(text: CharSequence, from: Int): Int? {
        var i = from
        while (i < text.length && text[i].isWhitespace()) i++
        return if (i < text.length) i else null
    }

    private fun lineOf(text: CharSequence, offset: Int): Int {
        var line = 0
        for (i in 0 until offset) if (text[i] == '\n') line++
        return line
    }
}
