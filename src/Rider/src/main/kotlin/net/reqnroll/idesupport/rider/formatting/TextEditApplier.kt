package net.reqnroll.idesupport.rider.formatting

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.TextEdit

/**
 * Applies LSP [TextEdit]s to a string without touching any platform `Document`, so it has no threading
 * rules and can run on a background formatting thread (issue #1200: the platform `applyTextEdits` helper
 * asserts the EDT).
 */
internal object TextEditApplier {
    /**
     * Returns the edited text, or `null` when the edits overlap (the server's list is then not trustworthy).
     * Positions are UTF-16 offsets as in LSP; a character past the end of a line clamps to the line end and
     * a line past the end of the text clamps to the end of the text. Line terminators are `\n`, `\r\n`, `\r`.
     */
    fun apply(text: String, edits: List<TextEdit>): String? {
        if (edits.isEmpty()) return text
        val lineStarts = lineStartOffsets(text)
        val resolved = edits.map { Triple(offsetOf(text, lineStarts, it.range.start), offsetOf(text, lineStarts, it.range.end), it.newText ?: "") }
            .sortedWith(compareBy({ it.first }, { it.second }))
        var previousEnd = 0
        for ((start, end, _) in resolved) {
            if (end < start || start < previousEnd) return null
            previousEnd = end
        }
        val result = StringBuilder(text.length)
        var cursor = 0
        for ((start, end, newText) in resolved) {
            result.append(text, cursor, start).append(newText)
            cursor = end
        }
        return result.append(text, cursor, text.length).toString()
    }

    private fun lineStartOffsets(text: String): List<Int> {
        val starts = arrayListOf(0)
        var i = 0
        while (i < text.length) {
            when (text[i]) {
                '\r' -> { if (i + 1 < text.length && text[i + 1] == '\n') i++; starts.add(i + 1) }
                '\n' -> starts.add(i + 1)
            }
            i++
        }
        return starts
    }

    private fun offsetOf(text: String, lineStarts: List<Int>, position: Position): Int {
        if (position.line >= lineStarts.size) return text.length
        val lineStart = lineStarts[maxOf(position.line, 0)]
        var lineEnd = if (position.line + 1 < lineStarts.size) lineStarts[position.line + 1] else text.length
        // exclude the terminator so a too-large character clamps to the end of the line's content
        while (lineEnd > lineStart && (text[lineEnd - 1] == '\n' || text[lineEnd - 1] == '\r')) lineEnd--
        return minOf(lineStart + maxOf(position.character, 0), lineEnd)
    }
}
