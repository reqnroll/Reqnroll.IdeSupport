package net.reqnroll.idesupport.rider.lsp

import org.eclipse.lsp4j.WorkspaceEdit
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
    fun `messageOf reads a -32803 rejection off a ServerError result`() {
        assertEquals("Not open", EditRejection.messageOf(LspRequestResult.ServerError(-32803, "Not open")))
        assertNull(EditRejection.messageOf(LspRequestResult.ServerError(-32603, "boom")))
        assertNull(EditRejection.messageOf(LspRequestResult.ServerError(-32803, " ")))
        assertNull(EditRejection.messageOf(LspRequestResult.TimedOut(10_000)))
        assertNull(EditRejection.messageOf(LspRequestResult.Success("x")))
    }

    @Test
    fun `a rename rejection is classified as Rejected with the server's reason`() {
        val outcome = ReqnrollRequestSender.renameOutcome(failure(responseError(-32803, "Parameter count mismatch")))

        assertEquals(RenameOutcome.Rejected("Parameter count mismatch"), outcome)
    }

    @Test
    fun `any other rename failure is the generic Failed outcome and never leaks the raw error`() {
        val outcome = ReqnrollRequestSender.renameOutcome(
            failure(responseError(-32603, "internal detail: NullReferenceException")))

        assertTrue(outcome is RenameOutcome.Failed)
        assertTrue(!(outcome as RenameOutcome.Failed).message.contains("NullReference"))
    }

    @Test
    fun `a rename timeout says it timed out`() {
        assertEquals(
            RenameOutcome.Failed("The rename request timed out."),
            ReqnrollRequestSender.renameOutcome(LspRequestResult.TimedOut(10_000)))
    }

    @Test
    fun `a rename with no server says the server is not running`() {
        val outcome = ReqnrollRequestSender.renameOutcome(LspRequestResult.Unavailable)

        assertTrue((outcome as RenameOutcome.Failed).message.contains("not running"))
    }

    @Test
    fun `a rename answered with an edit succeeds and a null answer fails`() {
        val edit = WorkspaceEdit()
        assertEquals(RenameOutcome.Success(edit), ReqnrollRequestSender.renameOutcome(LspRequestResult.Success(edit)))
        assertTrue(ReqnrollRequestSender.renameOutcome(LspRequestResult.Success(null)) is RenameOutcome.Failed)
    }

    @Test
    fun `a toggleComment rejection is classified as Rejected with the server's reason`() {
        val outcome = ReqnrollRequestSender.toggleCommentOutcome(
            failure(responseError(-32803, "The document is not open in the Reqnroll language server, so comments cannot be toggled.")))

        assertEquals(
            ToggleCommentOutcome.Rejected(
                "The document is not open in the Reqnroll language server, so comments cannot be toggled."),
            outcome)
    }

    @Test
    fun `any other toggleComment failure is Failed`() {
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.toggleCommentOutcome(failure(responseError(-32603, "boom"))))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.toggleCommentOutcome(failure(responseError(-32801, "Content Modified"))))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.toggleCommentOutcome(failure(java.io.IOException("pipe"))))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.toggleCommentOutcome(LspRequestResult.TimedOut(10_000)))
        assertEquals(ToggleCommentOutcome.Failed, ReqnrollRequestSender.toggleCommentOutcome(LspRequestResult.Unavailable))
    }

    @Test
    fun `a dispatched toggleComment is Dispatched even with a null answer`() {
        assertEquals(ToggleCommentOutcome.Dispatched, ReqnrollRequestSender.toggleCommentOutcome(LspRequestResult.Success(null)))
    }

    @Test
    fun `a formatting rejection is classified as Rejected with the server's reason`() {
        assertEquals(
            FormatOutcome.Rejected("Cannot format: no Feature"),
            ReqnrollRequestSender.formatOutcome(failure(responseError(-32803, "Cannot format: no Feature"))))
    }

    @Test
    fun `any other formatting failure is Failed`() {
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.formatOutcome(failure(responseError(-32603, "boom"))))
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.formatOutcome(failure(responseError(-32801, "Content Modified"))))
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.formatOutcome(failure(java.io.IOException("pipe"))))
        assertEquals(FormatOutcome.Failed, ReqnrollRequestSender.formatOutcome(LspRequestResult.TimedOut(10_000)))
    }

    @Test
    fun `a formatting answer of null is no edits`() {
        assertEquals(FormatOutcome.Edits(emptyList()), ReqnrollRequestSender.formatOutcome(LspRequestResult.Success(null)))
    }

    private fun failure(ex: Throwable): LspRequestResult<Nothing> = LspRequestResult.fromException(ex)
}
