package com.reqnroll.ide.rider.actions

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.impl.DocumentImpl
import com.intellij.testFramework.ApplicationRule
import org.junit.ClassRule
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

/**
 * Regression coverage for issue #980: the four position-based Rider actions must send the LSP
 * `character` as a UTF-16 offset within the line, not IntelliJ's tab-expanded
 * `LogicalPosition.column`. In a tab-indented file the two disagree, so the buggy column shifts
 * every request to the right and makes the server look up the wrong element.
 */
class LspCaretPositionTest {
    companion object {
        @JvmField
        @ClassRule
        val applicationRule = ApplicationRule()
    }

    @Test
    fun `editor caret after a tab sends the raw within-line offset, not the tab-expanded column`() {
        // A leading tab followed by "Csharp". IntelliJ's LogicalPosition.column counts the tab as
        // its configured tab size (4), so the buggy column is 1 + 4 + 6 = 11; the LSP character
        // must instead count the tab as a single UTF-16 code unit (1 + 6 = 7).
        var line = -1
        var character = -1
        var logicalColumn = -1
        ApplicationManager.getApplication().invokeAndWait {
            val document = EditorFactory.getInstance().createDocument("\tCsharp step\n")
            val editor = EditorFactory.getInstance().createEditor(document)
            try {
                val offset = document.getLineStartOffset(0) + 1 + "Csharp".length
                editor.caretModel.moveToOffset(offset)

                val position = lspCaretPosition(editor)
                line = position.line
                character = position.character
                logicalColumn = editor.caretModel.logicalPosition.column
            } finally {
                EditorFactory.getInstance().releaseEditor(editor)
            }
        }

        assertEquals(0, line)
        assertEquals(1 + "Csharp".length, character)
        // The bug being fixed: the old code sent this tab-expanded column instead.
        assertNotEquals(logicalColumn, character)
    }

    @Test
    fun `character is the raw within-line offset for a tab-indented document`() {
        val document = DocumentImpl("\tCsharp step\n")
        val offset = document.getLineStartOffset(0) + 1 + "Csharp".length

        val position = lspCaretPosition(document, offset)

        assertEquals(0, position.line)
        assertEquals(1 + "Csharp".length, position.character)
    }

    @Test
    fun `caret at the line start has character zero`() {
        val document = DocumentImpl("First\n\tSecond\n")

        val position = lspCaretPosition(document, document.getLineStartOffset(1))

        assertEquals(1, position.line)
        assertEquals(0, position.character)
    }

    @Test
    fun `line index and character track a space-indented second line`() {
        val document = DocumentImpl("line one\n    indented\n")
        val offset = document.getLineStartOffset(1) + 4 + "indented".length

        val position = lspCaretPosition(document, offset)

        assertEquals(1, position.line)
        assertEquals(4 + "indented".length, position.character)
    }
}
