package net.reqnroll.idesupport.rider.lsp

import org.eclipse.lsp4j.jsonrpc.ResponseErrorException

/**
 * What came back from one request sent by [ReqnrollRequestSender]. Keeps an answer apart from a failure, so a caller
 * that caches or acts on the result never mistakes a server error, a timeout or a missing server for "nothing here".
 *
 * A JSON `null` answer is still a [Success] (with a `null` [Success.value]): the server answered, and for most LSP
 * requests `null` means "no result".
 */
sealed interface LspRequestResult<out T> {
    /** The server answered; [value] is its (possibly `null`) result. */
    data class Success<out T>(val value: T?) : LspRequestResult<T>

    /** The server answered with a JSON-RPC error. */
    data class ServerError(val code: Int, val message: String) : LspRequestResult<Nothing>

    /** No answer arrived within [timeoutMs]; the request was cancelled. */
    data class TimedOut(val timeoutMs: Int) : LspRequestResult<Nothing>

    /** No Reqnroll LSP server is running for the project, so nothing was sent. */
    data object Unavailable : LspRequestResult<Nothing>

    /** The request failed on the client side or in transport (for example the connection closed). */
    data class Failed(val cause: Throwable) : LspRequestResult<Nothing>

    companion object {
        /**
         * Maps an exception thrown while awaiting a response to its result: a [ResponseErrorException] (bare, or one
         * level down as a cause) to [ServerError], anything else to [Failed].
         */
        fun fromException(ex: Throwable): LspRequestResult<Nothing> {
            val error = ((ex as? ResponseErrorException) ?: (ex.cause as? ResponseErrorException))?.responseError
            return if (error != null) ServerError(error.code, error.message.orEmpty()) else Failed(ex)
        }
    }
}

/** The answer, or `null` both for a `null` answer and for any failure. For callers with nothing to show or keep. */
fun <T> LspRequestResult<T>.valueOrNull(): T? = (this as? LspRequestResult.Success)?.value

/** Transforms a [LspRequestResult.Success] value; failures pass through unchanged. */
inline fun <T, R> LspRequestResult<T>.map(transform: (T?) -> R?): LspRequestResult<R> = when (this) {
    is LspRequestResult.Success -> LspRequestResult.Success(transform(value))
    is LspRequestResult.ServerError -> this
    is LspRequestResult.TimedOut -> this
    LspRequestResult.Unavailable -> LspRequestResult.Unavailable
    is LspRequestResult.Failed -> this
}

/** A sentence for the user saying why the request produced no answer, or `null` for a [LspRequestResult.Success]. */
val LspRequestResult<*>.failureMessage: String?
    get() = when (this) {
        is LspRequestResult.Success -> null
        LspRequestResult.Unavailable -> "The Reqnroll LSP server is not running."
        is LspRequestResult.TimedOut -> "The Reqnroll LSP server did not respond within ${timeoutMs / 1000} seconds."
        // The server's own message can carry internal exception detail, so it goes to the log (see
        // ReqnrollRequestSender), not to the user.
        is LspRequestResult.ServerError ->
            "The Reqnroll LSP server could not answer the request (error $code). See the Reqnroll log for details."
        is LspRequestResult.Failed -> "The request to the Reqnroll LSP server failed."
    }

/**
 * A sentence for the user when a caller that needs an answer got none: the [failureMessage], or for a `null`
 * [LspRequestResult.Success] a note that the server answered without a result.
 */
val LspRequestResult<*>.noAnswerMessage: String
    get() = failureMessage ?: "The Reqnroll LSP server returned no result."
