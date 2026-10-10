package net.reqnroll.idesupport.rider.commenting

import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollToggleCommentActionTest {
    @Test
    fun `normalizeSelectionLines keeps the end line when it has selected characters`() {
        assertEquals(
            2 to 5,
            ReqnrollToggleCommentAction.normalizeSelectionLines(startLine = 2, endLine = 5, endChar = 3),
        )
    }

    @Test
    fun `normalizeSelectionLines drops the end line when the selection stops at column 0`() {
        assertEquals(
            2 to 4,
            ReqnrollToggleCommentAction.normalizeSelectionLines(startLine = 2, endLine = 5, endChar = 0),
        )
    }

    @Test
    fun `normalizeSelectionLines keeps a single-line selection even at column 0`() {
        assertEquals(
            3 to 3,
            ReqnrollToggleCommentAction.normalizeSelectionLines(startLine = 3, endLine = 3, endChar = 0),
        )
    }
}
