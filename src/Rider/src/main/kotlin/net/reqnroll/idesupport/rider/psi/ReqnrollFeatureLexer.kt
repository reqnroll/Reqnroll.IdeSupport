package net.reqnroll.idesupport.rider.psi

import com.intellij.lexer.LexerBase
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType

/**
 * Lexes a feature file by walking it with the Cucumber Gherkin parser ([FeatureSyntax]) and emitting one token
 * per reported span. Gaps between spans become whitespace, or [ReqnrollFeatureTokenTypes.UNMATCHED] for text the
 * parser skipped, so the tokens always cover the whole buffer without overlap.
 *
 * Gherkin's state (dialect, doc-string nesting) depends on everything before a position, so every [start]
 * re-walks the whole buffer and keeps the tokens that fall inside the requested range. The reported state is
 * always 0, which makes any token boundary a valid restart point.
 */
class ReqnrollFeatureLexer(
    private val defaultLanguage: String = FeatureSyntax.FALLBACK_LANGUAGE,
) : LexerBase() {
    private class Segment(val type: IElementType, val start: Int, val end: Int)

    private var buffer: CharSequence = ""
    private var endOffset = 0
    private var segments: List<Segment> = emptyList()
    private var index = 0

    override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
        this.buffer = buffer
        this.endOffset = endOffset
        segments = segmentsOf(buffer, defaultLanguage).mapNotNull { clip(it, startOffset, endOffset) }
        index = 0
    }

    override fun getState(): Int = 0

    override fun getTokenType(): IElementType? = segments.getOrNull(index)?.type

    override fun getTokenStart(): Int = segments[index].start

    override fun getTokenEnd(): Int = segments[index].end

    override fun advance() {
        index++
    }

    override fun getBufferSequence(): CharSequence = buffer

    override fun getBufferEnd(): Int = endOffset

    private fun clip(segment: Segment, from: Int, to: Int): Segment? {
        val start = maxOf(segment.start, from)
        val end = minOf(segment.end, to)
        return if (start < end) Segment(segment.type, start, end) else null
    }

    private fun segmentsOf(text: CharSequence, language: String): List<Segment> {
        val result = ArrayList<Segment>()
        var position = 0
        for (event in FeatureSyntax.parse(text, language)) {
            if (event !is SyntaxEvent.Token) continue
            if (event.start < position || event.end > text.length || event.start >= event.end) continue
            fillGap(text, position, event.start, result)
            result.add(Segment(ReqnrollFeatureTokenTypes.of(event.kind), event.start, event.end))
            position = event.end
        }
        fillGap(text, position, text.length, result)
        return result
    }

    private fun fillGap(text: CharSequence, from: Int, to: Int, into: MutableList<Segment>) {
        var i = from
        while (i < to) {
            if (text[i].isWhitespace()) {
                var j = i
                while (j < to && text[j].isWhitespace()) j++
                into.add(Segment(TokenType.WHITE_SPACE, i, j))
                i = j
            } else {
                var j = i
                var lastNonWhitespace = i
                while (j < to && text[j] != '\n' && text[j] != '\r') {
                    if (!text[j].isWhitespace()) lastNonWhitespace = j + 1
                    j++
                }
                into.add(Segment(ReqnrollFeatureTokenTypes.UNMATCHED, i, lastNonWhitespace))
                i = lastNonWhitespace
            }
        }
    }
}
