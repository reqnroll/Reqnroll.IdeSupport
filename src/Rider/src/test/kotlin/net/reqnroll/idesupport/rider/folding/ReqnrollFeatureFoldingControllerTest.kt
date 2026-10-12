package net.reqnroll.idesupport.rider.folding

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollFeatureFoldingControllerTest {
    @Test
    fun `isFoldable accepts an in-range span of more than one line`() {
        assertTrue(ReqnrollFeatureFoldingController.isFoldable(lineCount = 10, startLine = 2, endLine = 5))
    }

    @Test
    fun `isFoldable rejects a single-line or inverted span`() {
        assertFalse(ReqnrollFeatureFoldingController.isFoldable(lineCount = 10, startLine = 2, endLine = 2))
        assertFalse(ReqnrollFeatureFoldingController.isFoldable(lineCount = 10, startLine = 5, endLine = 2))
    }

    @Test
    fun `isFoldable rejects lines outside the document`() {
        assertFalse(ReqnrollFeatureFoldingController.isFoldable(lineCount = 10, startLine = -1, endLine = 5))
        assertFalse(ReqnrollFeatureFoldingController.isFoldable(lineCount = 10, startLine = 2, endLine = 10))
        assertFalse(ReqnrollFeatureFoldingController.isFoldable(lineCount = 0, startLine = 0, endLine = 0))
    }
}
