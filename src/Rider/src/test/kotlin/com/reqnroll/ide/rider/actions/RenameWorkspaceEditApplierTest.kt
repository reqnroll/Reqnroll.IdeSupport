package com.reqnroll.ide.rider.actions

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.RenameFile
import org.eclipse.lsp4j.TextDocumentEdit
import org.eclipse.lsp4j.TextEdit
import org.eclipse.lsp4j.VersionedTextDocumentIdentifier
import org.eclipse.lsp4j.WorkspaceEdit
import org.eclipse.lsp4j.jsonrpc.messages.Either
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class RenameWorkspaceEditApplierTest {
    private fun textEdit(line: Int, char: Int, newText: String) =
        TextEdit(Range(Position(line, char), Position(line, char)), newText)

    @Test
    fun `editsByUri groups documentChanges edits per uri`() {
        val featureEdit = TextDocumentEdit(
            VersionedTextDocumentIdentifier("file:///repo/Calculator.feature", null),
            listOf(textEdit(3, 4, "the sum is {int}")),
        )
        val csEdit = TextDocumentEdit(
            VersionedTextDocumentIdentifier("file:///repo/CalculatorSteps.cs", null),
            listOf(textEdit(10, 8, "the sum is {int}")),
        )
        val edit = WorkspaceEdit(
            listOf(Either.forLeft(featureEdit), Either.forLeft(csEdit)),
        )

        val result = RenameWorkspaceEditApplier.editsByUri(edit)

        assertEquals(setOf("file:///repo/Calculator.feature", "file:///repo/CalculatorSteps.cs"), result.keys)
        assertEquals(1, result["file:///repo/Calculator.feature"]!!.size)
        assertEquals(1, result["file:///repo/CalculatorSteps.cs"]!!.size)
    }

    @Test
    fun `editsByUri flattens multiple documentChanges entries for the same uri`() {
        val uri = "file:///repo/Calculator.feature"
        val first = TextDocumentEdit(VersionedTextDocumentIdentifier(uri, null), listOf(textEdit(1, 0, "a")))
        val second = TextDocumentEdit(VersionedTextDocumentIdentifier(uri, null), listOf(textEdit(5, 0, "b")))
        val edit = WorkspaceEdit(listOf(Either.forLeft(first), Either.forLeft(second)))

        val result = RenameWorkspaceEditApplier.editsByUri(edit)

        assertEquals(2, result[uri]!!.size)
    }

    @Test
    fun `editsByUri falls back to the legacy changes map when documentChanges is absent`() {
        val edit = WorkspaceEdit(
            mapOf("file:///repo/Calculator.feature" to listOf(textEdit(3, 4, "the sum is {int}"))),
        )

        val result = RenameWorkspaceEditApplier.editsByUri(edit)

        assertEquals(setOf("file:///repo/Calculator.feature"), result.keys)
        assertEquals(1, result["file:///repo/Calculator.feature"]!!.size)
    }

    @Test
    fun `editsByUri drops ResourceOperation entries and keeps TextDocumentEdit entries`() {
        val featureEdit = TextDocumentEdit(
            VersionedTextDocumentIdentifier("file:///repo/Calculator.feature", null),
            listOf(textEdit(3, 4, "the sum is {int}")),
        )
        val renameOperation = RenameFile().apply {
            oldUri = "file:///repo/Old.feature"
            newUri = "file:///repo/New.feature"
        }
        val edit = WorkspaceEdit(
            listOf(Either.forLeft(featureEdit), Either.forRight(renameOperation)),
        )

        val result = RenameWorkspaceEditApplier.editsByUri(edit)

        assertEquals(setOf("file:///repo/Calculator.feature"), result.keys)
        assertEquals(1, result["file:///repo/Calculator.feature"]!!.size)
        assertTrue("file:///repo/Old.feature" !in result)
        assertTrue("file:///repo/New.feature" !in result)
    }

    @Test
    fun `editsByUri returns emptyMap when both documentChanges and changes are null`() {
        val edit = WorkspaceEdit()

        val result = RenameWorkspaceEditApplier.editsByUri(edit)

        assertEquals(emptyMap(), result)
    }

    @Test
    fun `orderForApplication sorts edits in reverse document order`() {
        val edits = listOf(
            textEdit(1, 0, "first"),
            textEdit(10, 2, "third"),
            textEdit(10, 0, "second"),
        )

        val ordered = RenameWorkspaceEditApplier.orderForApplication(edits)

        assertEquals(listOf("third", "second", "first"), ordered.map { it.newText })
    }
}
