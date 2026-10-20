package net.reqnroll.idesupport.rider.lsp

import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import org.eclipse.lsp4j.jsonrpc.messages.ResponseError
import java.util.concurrent.ExecutionException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class EditRejectionTest {
    private fun responseError(code: Int, message: String?) =
        ResponseErrorException(ResponseError(code, message, null))

    @Test
    fun `the code is the LSP RequestFailed code`() {
        assertEquals(-32803, EditRejection.ERROR_CODE)
    }

    @Test
    fun `messageOf reads the message off a direct -32803 ResponseErrorException`() {
        // The shape textDocument/rename (and executeCommand) now fail with:
        // the server throwing RpcErrorException becomes a JSON-RPC error response, which LSP4J
        // surfaces client-side as a ResponseErrorException.
        assertEquals("Parameter count mismatch", EditRejection.messageOf(responseError(-32803, "Parameter count mismatch")))
    }

    @Test
    fun `messageOf unwraps one level of cause`() {
        val wrapped = ExecutionException(responseError(-32803, "The project is not initialized yet"))

        assertEquals("The project is not initialized yet", EditRejection.messageOf(wrapped))
    }

    @Test
    fun `messageOf is null for any other error code including ContentModified`() {
        for (code in listOf(-32801, -32603, -32800, 0)) {
            assertNull(EditRejection.messageOf(responseError(code, "something")), "code $code")
        }
    }

    @Test
    fun `messageOf is null for a blank or missing message`() {
        assertNull(EditRejection.messageOf(responseError(-32803, "   ")))
        assertNull(EditRejection.messageOf(responseError(-32803, null)))
    }

    @Test
    fun `messageOf is null for an unrelated failure`() {
        assertNull(EditRejection.messageOf(java.io.IOException("timeout")))
        assertNull(EditRejection.messageOf(ExecutionException(RuntimeException("something unrelated"))))
    }

    @Test
    fun `a rename rejection is classified as Rejected with the server's reason`() {
        val outcome = ReqnrollRequestSender.classifyRenameFailure(responseError(-32803, "Parameter count mismatch"))

        assertEquals(RenameOutcome.Rejected("Parameter count mismatch"), outcome)
    }

    @Test
    fun `any other rename failure is the generic Failed outcome and never leaks the raw error`() {
        val outcome = ReqnrollRequestSender.classifyRenameFailure(
            responseError(-32603, "internal detail: NullReferenceException"))

        assertTrue(outcome is RenameOutcome.Failed)
        assertTrue(!(outcome as RenameOutcome.Failed).message.contains("NullReference"))
    }

    @Test
    fun `a toggleComment rejection is classified as Rejected with the server's reason`() {
        val outcome = ReqnrollRequestSender.classifyToggleCommentFailure(
            responseError(-32803, "The document is not open in the Reqnroll language server, so comments cannot be toggled."))

        assertEquals(
            ToggleCommentOutcome.Rejected(
                "The document is not open in the Reqnroll language server, so comments cannot be toggled."),
            outcome)
    }

    @Test
    fun `any other toggleComment failure is Failed`() {
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.classifyToggleCommentFailure(responseError(-32603, "boom")))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.classifyToggleCommentFailure(responseError(-32801, "Content Modified")))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.classifyToggleCommentFailure(java.io.IOException("pipe")))
    }

    @Test
    fun `a formatting rejection is classified as Rejected with the server's reason`() {
        assertEquals(
            FormatOutcome.Rejected("Cannot format: no Feature"),
            ReqnrollRequestSender.classifyFormatFailure(responseError(-32803, "Cannot format: no Feature")))
    }

    @Test
    fun `any other formatting failure is Failed`() {
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.classifyFormatFailure(responseError(-32603, "boom")))
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.classifyFormatFailure(responseError(-32801, "Content Modified")))
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.classifyFormatFailure(java.io.IOException("pipe")))
    }
}
