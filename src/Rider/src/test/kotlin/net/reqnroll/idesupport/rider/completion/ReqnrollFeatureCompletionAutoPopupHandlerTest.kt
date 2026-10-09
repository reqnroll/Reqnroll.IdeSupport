package net.reqnroll.idesupport.rider.completion

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollFeatureCompletionAutoPopupHandlerTest {
    @Test
    fun `tag and space characters start the popup`() {
        assertTrue(ReqnrollFeatureCompletionAutoPopupHandler.isAutoPopupTrigger('@'))
        assertTrue(ReqnrollFeatureCompletionAutoPopupHandler.isAutoPopupTrigger(' '))
    }

    @Test
    fun `other characters do not`() {
        for (c in listOf('a', '1', '|', '\n', '\t', '#', ':')) {
            assertFalse(ReqnrollFeatureCompletionAutoPopupHandler.isAutoPopupTrigger(c), "char '$c'")
        }
    }
}
