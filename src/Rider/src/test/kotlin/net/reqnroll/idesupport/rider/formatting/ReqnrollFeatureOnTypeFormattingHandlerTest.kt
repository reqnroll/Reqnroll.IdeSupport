package net.reqnroll.idesupport.rider.formatting

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.TextEdit
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollFeatureOnTypeFormattingHandlerTest {
    private fun edit(startLine: Int, startChar: Int = 0, endLine: Int = startLine, endChar: Int = 0) =
        TextEdit(Range(Position(startLine, startChar), Position(endLine, endChar)), "text")

    @Test
    fun `orderForApplication sorts by descending line so earlier edits' offsets stay valid`() {
        val early = edit(startLine = 1)
        val middle = edit(startLine = 3)
        val late = edit(startLine = 5)

        assertEquals(listOf(late, middle, early), ReqnrollFeatureOnTypeFormattingHandler.orderForApplication(listOf(early, late, middle)))
    }

    @Test
    fun `orderForApplication breaks ties on the same line by descending character`() {
        val leftCell = edit(startLine = 2, startChar = 0)
        val rightCell = edit(startLine = 2, startChar = 10)

        assertEquals(
            listOf(rightCell, leftCell),
            ReqnrollFeatureOnTypeFormattingHandler.orderForApplication(listOf(leftCell, rightCell)),
        )
    }

    @Test
    fun `orderForApplication is stable for an already-ordered or empty list`() {
        assertEquals(emptyList(), ReqnrollFeatureOnTypeFormattingHandler.orderForApplication(emptyList()))

        val single = listOf(edit(startLine = 1))
        assertEquals(single, ReqnrollFeatureOnTypeFormattingHandler.orderForApplication(single))
    }
}
