package com.reqnroll.ide.rider.actions

import java.awt.Color
import javax.swing.JComponent
import javax.swing.JList
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

/**
 * Exercises [ReqnrollResultPopup.resultListCellRenderer] directly (no platform/popup fixture
 * needed): the renderer is plain Swing, so we can drive its `ListCellRenderer` contract by hand
 * and assert the selection cue that issue #992 was about.
 */
class ReqnrollResultPopupTest {

    private val selectionBackground = Color(0x33, 0x66, 0xCC)

    private fun <T> list(): JList<T> =
        JList<T>().apply {
            background = Color.WHITE
            foreground = Color.BLACK
            selectionBackground = this@ReqnrollResultPopupTest.selectionBackground
            selectionForeground = Color.WHITE
        }

    private fun <T> render(list: JList<T>, value: T, selected: Boolean) =
        ReqnrollResultPopup.resultListCellRenderer<T> { "row: $it" }
            .getListCellRendererComponent(list, value, 0, selected, false)

    @Test
    fun `renderer shows the row text produced by the render function`() {
        val component = render(list(), "abc", selected = false)
        assertEquals("row: abc", component.toString())
    }

    @Test
    fun `selected row uses the list's selection background so keyboard navigation has a visible cue`() {
        val list = list<String>()
        val selectedBackground = render(list, "abc", selected = true).background
        assertEquals(list.selectionBackground, selectedBackground)
    }

    @Test
    fun `selected row is opaque so the selection background is actually painted`() {
        val component = render(list(), "abc", selected = true) as JComponent
        assertTrue(component.isOpaque, "a non-opaque renderer cannot paint the selection background")
    }

    @Test
    fun `unselected row does not use the selection background`() {
        val list = list<String>()
        val unselectedBackground = render(list, "abc", selected = false).background
        assertNotEquals(list.selectionBackground, unselectedBackground)
    }
}
