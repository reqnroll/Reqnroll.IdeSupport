package net.reqnroll.idesupport.rider.formatting

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.TextEdit
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class TextEditApplierTest {
    private fun edit(sl: Int, sc: Int, el: Int, ec: Int, text: String) =
        TextEdit(Range(Position(sl, sc), Position(el, ec)), text)

    @Test
    fun `no edits returns the text unchanged`() {
        assertEquals("a\nb", TextEditApplier.apply("a\nb", emptyList()))
    }

    @Test
    fun `multiple edits are applied regardless of order`() {
        val result = TextEditApplier.apply(
            "Feature: x\n  Given a\n  When b",
            listOf(edit(2, 0, 2, 2, "    "), edit(1, 0, 1, 2, "    "), edit(0, 9, 0, 10, "y")),
        )
        assertEquals("Feature: y\n    Given a\n    When b", result)
    }

    @Test
    fun `an insertion at a position is applied`() {
        assertEquals("ab\ncd", TextEditApplier.apply("a\ncd", listOf(edit(0, 1, 0, 1, "b"))))
    }

    @Test
    fun `CRLF line endings are respected`() {
        assertEquals("a\r\n  b\r\nc", TextEditApplier.apply("a\r\nb\r\nc", listOf(edit(1, 0, 1, 0, "  "))))
    }

    @Test
    fun `a character past the line end clamps before the terminator`() {
        assertEquals("a!\r\nb", TextEditApplier.apply("a\r\nb", listOf(edit(0, 99, 0, 99, "!"))))
    }

    @Test
    fun `a line past the end clamps to the end of the text`() {
        assertEquals("a\nb!", TextEditApplier.apply("a\nb", listOf(edit(9, 0, 9, 0, "!"))))
    }

    @Test
    fun `overlapping edits are rejected`() {
        assertNull(TextEditApplier.apply("abcdef", listOf(edit(0, 0, 0, 4, "x"), edit(0, 2, 0, 5, "y"))))
    }

    @Test
    fun `a whole-document replace works`() {
        assertEquals("new", TextEditApplier.apply("old\ntext", listOf(edit(0, 0, 1, 4, "new"))))
    }
}
