package com.reqnroll.ide.rider.console

import com.intellij.execution.ui.ConsoleViewContentType
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollConsolePanelTest {
    @Test
    fun `contentTypeForLevel maps Error to the LOG_ERROR content type`() {
        assertEquals(ConsoleViewContentType.LOG_ERROR_OUTPUT, contentTypeForLevel("Error"))
    }

    @Test
    fun `contentTypeForLevel maps Warning to the LOG_WARNING content type`() {
        assertEquals(ConsoleViewContentType.LOG_WARNING_OUTPUT, contentTypeForLevel("Warning"))
    }

    @Test
    fun `contentTypeForLevel maps Info (and anything else) to plain NORMAL_OUTPUT`() {
        assertEquals(ConsoleViewContentType.NORMAL_OUTPUT, contentTypeForLevel("Info"))
        assertEquals(ConsoleViewContentType.NORMAL_OUTPUT, contentTypeForLevel("Verbose"))
    }
}
