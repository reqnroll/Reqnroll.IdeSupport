package com.reqnroll.ide.rider.actions

import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor

/**
 * A zero-based LSP caret position — `line` is the zero-based line index and `character` is the
 * UTF-16 offset within that line, exactly matching the LSP `Position` shape.
 */
data class LspCaretPosition(val line: Int, val character: Int)

/**
 * Computes the LSP position for [editor]'s caret.
 *
 * An LSP `character` is a UTF-16 offset within the line, but IntelliJ's
 * [com.intellij.openapi.editor.LogicalPosition.column] expands tabs to the editor's configured
 * tab size. In tab-indented files the two disagree, so sending `logicalPosition.column` shifts
 * the request position right and makes the position-based `reqnroll/findStepUsages`,
 * `reqnroll/findHooks` and `reqnroll/renameTargets` requests miss or hit the wrong element
 * (issue #980). Use the raw caret offset minus the line's start offset instead — the same
 * offset-based math the on-type-formatting handler and the breadcrumbs collector already use.
 */
fun lspCaretPosition(editor: Editor): LspCaretPosition =
    lspCaretPosition(editor.document, editor.caretModel.offset)

internal fun lspCaretPosition(document: Document, offset: Int): LspCaretPosition {
    val line = document.getLineNumber(offset)
    val character = offset - document.getLineStartOffset(line)
    return LspCaretPosition(line, character)
}
