package net.reqnroll.idesupport.rider.lsp

import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import org.eclipse.lsp4j.jsonrpc.messages.ResponseError
import java.util.concurrent.CompletionException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class LspRequestResultTest {
    private fun responseError(code: Int, message: String?) = ResponseErrorException(ResponseError(code, message, null))

    @Test
    fun `a JSON-RPC error response is a ServerError with its code and message`() {
        assertEquals(LspRequestResult.ServerError(-32603, "boom"), LspRequestResult.fromException(responseError(-32603, "boom")))
    }

    @Test
    fun `a JSON-RPC error one level down is still a ServerError`() {
        val wrapped = CompletionException(responseError(-32801, "Content Modified"))

        assertEquals(LspRequestResult.ServerError(-32801, "Content Modified"), LspRequestResult.fromException(wrapped))
    }

    @Test
    fun `a missing error message becomes an empty one`() {
        assertEquals(LspRequestResult.ServerError(-32603, ""), LspRequestResult.fromException(responseError(-32603, null)))
    }

    @Test
    fun `any other exception is Failed and keeps its cause`() {
        val ex = java.io.IOException("pipe closed")

        val result = LspRequestResult.fromException(ex)

        assertSame(ex, (result as LspRequestResult.Failed).cause)
    }

    @Test
    fun `valueOrNull is the answer for a Success and null for every failure`() {
        assertEquals("x", LspRequestResult.Success("x").valueOrNull())
        assertNull(LspRequestResult.Success<String>(null).valueOrNull())
        assertNull(LspRequestResult.ServerError(-32603, "boom").valueOrNull())
        assertNull(LspRequestResult.TimedOut(10_000).valueOrNull())
        assertNull(LspRequestResult.Unavailable.valueOrNull())
        assertNull(LspRequestResult.Failed(RuntimeException()).valueOrNull())
    }

    @Test
    fun `map transforms a Success and passes failures through unchanged`() {
        assertEquals(LspRequestResult.Success(3), LspRequestResult.Success("abc").map { it?.length })
        assertEquals(LspRequestResult.Success(null), LspRequestResult.Success<String>(null).map { it?.length })

        val failures = listOf(
            LspRequestResult.ServerError(-32603, "boom"),
            LspRequestResult.TimedOut(10_000),
            LspRequestResult.Unavailable,
            LspRequestResult.Failed(RuntimeException()),
        )
        for (failure in failures) {
            assertEquals<LspRequestResult<Int>>(failure, failure.map<String, Int> { error("must not be called") })
        }
    }

    @Test
    fun `a Success has no failure message, even with a null answer`() {
        assertNull(LspRequestResult.Success("x").failureMessage)
        assertNull(LspRequestResult.Success<String>(null).failureMessage)
    }

    @Test
    fun `each failure says why there is no answer`() {
        assertEquals("The Reqnroll LSP server is not running.", LspRequestResult.Unavailable.failureMessage)
        assertEquals(
            "The Reqnroll LSP server did not respond within 10 seconds.", LspRequestResult.TimedOut(10_000).failureMessage)
        assertEquals(
            "The Reqnroll LSP server could not answer the request (error -32603). See the Reqnroll log for details.",
            LspRequestResult.ServerError(-32603, "Project not loaded").failureMessage)
        assertEquals("The request to the Reqnroll LSP server failed.", LspRequestResult.Failed(RuntimeException()).failureMessage)
    }

    @Test
    fun `a server error message never reaches the user-facing text`() {
        val text = LspRequestResult.ServerError(-32603, "NullReferenceException at Reqnroll.Internal.Foo").failureMessage!!

        assertTrue(!text.contains("NullReference"))
    }

    @Test
    fun `noAnswerMessage covers a null answer as well as failures`() {
        assertEquals("The Reqnroll LSP server returned no result.", LspRequestResult.Success<String>(null).noAnswerMessage)
        assertEquals(LspRequestResult.Unavailable.failureMessage, LspRequestResult.Unavailable.noAnswerMessage)
        assertTrue(LspRequestResult.TimedOut(30_000).noAnswerMessage.contains("30 seconds"))
    }
}
