package net.reqnroll.idesupport.rider.lsp

/**
 * Client side of the server's "edit rejected" contract: a JSON-RPC error with code
 * [ERROR_CODE] (LSP `RequestFailed`) and a non-blank message is a rejection the user can act on, and
 * its message is meant to be shown verbatim (in a modal, via
 * [net.reqnroll.idesupport.rider.actions.ReqnrollNotify.editRejected]). Every other error code is an
 * unexpected failure — log it, show at most a generic message — and `ContentModified` (-32801) is a
 * routine retryable cancellation that must never reach a dialog.
 */
internal object EditRejection {
    /** The LSP `RequestFailed` error code that marks a user-correctable rejection. */
    const val ERROR_CODE = -32803

    /**
     * The rejection message carried by [ex] when it is — or, one level down, is caused by — a
     * [ResponseErrorException] with code [ERROR_CODE] and a non-blank message; otherwise `null`.
     *
     * Checks one level of [Throwable.cause] as cheap insurance against a call site that wraps the
     * exception again; the suspend `sendRequest` used by the senders delivers it bare.
     */
    fun messageOf(ex: Throwable): String? = messageOf(LspRequestResult.fromException(ex))

    /** The rejection message carried by [result] when it is a [LspRequestResult.ServerError] with code [ERROR_CODE] and a non-blank message; otherwise `null`. */
    fun messageOf(result: LspRequestResult<*>): String? =
        (result as? LspRequestResult.ServerError)
            ?.takeIf { it.code == ERROR_CODE && it.message.isNotBlank() }
            ?.message
}
