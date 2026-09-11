package com.reqnroll.ide.rider.lsp

import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import org.eclipse.lsp4j.jsonrpc.messages.ResponseError
import java.util.concurrent.ExecutionException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ReqnrollRequestSenderTest {
    @Test
    fun `extractResponseErrorMessage reads the message off a direct ResponseErrorException`() {
        // The shape textDocument-rename now fails with (issue #650) — RenameHandler throwing
        // RpcErrorException server-side becomes a JSON-RPC error response, which LSP4J surfaces
        // client-side as a ResponseErrorException.
        val ex = ResponseErrorException(ResponseError(-32803, "Parameter count mismatch", "rename"))

        assertEquals("Parameter count mismatch", ReqnrollRequestSender.extractResponseErrorMessage(ex))
    }

    @Test
    fun `extractResponseErrorMessage unwraps one level of cause`() {
        // LspServer#sendRequestSync gives no documented guarantee about whether it surfaces the
        // ResponseErrorException directly or wrapped (e.g. an ExecutionException from the
        // underlying CompletableFuture#get) - both shapes must resolve to the real message.
        val responseError = ResponseErrorException(ResponseError(-32803, "The project is not initialized yet", null))
        val wrapped = ExecutionException(responseError)

        assertEquals("The project is not initialized yet", ReqnrollRequestSender.extractResponseErrorMessage(wrapped))
    }

    @Test
    fun `extractResponseErrorMessage returns null for an unrelated failure`() {
        assertNull(ReqnrollRequestSender.extractResponseErrorMessage(java.io.IOException("timeout")))
    }

    @Test
    fun `extractResponseErrorMessage returns null when nothing in the chain is a ResponseErrorException`() {
        val wrapped = ExecutionException(RuntimeException("something unrelated"))

        assertNull(ReqnrollRequestSender.extractResponseErrorMessage(wrapped))
    }
}
